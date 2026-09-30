// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Content.Client.Parallax.Data;
using Robust.Client.Graphics;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using YamlDotNet.RepresentationModel;

namespace Content.IntegrationTests.Tests.Parallax;

/// <summary>
/// Guards the DeepOcean parallax: the base background of the game, and the one
/// background that must never silently fail to load.
/// </summary>
/// <remarks>
/// These assertions exist because the failure modes here are invisible. A missing
/// texture, a mistyped shader id or a shader param that no longer matches a uniform
/// all degrade to a transparent or flat layer at runtime with nothing but a log line
/// to show for it, and the game looks subtly wrong rather than broken.
///
/// Deliberately one test borrowing one client/server pair: booting a pair costs
/// most of a minute, and splitting these up would quadruple that for no benefit.
/// </remarks>
[TestFixture]
[TestOf(typeof(ParallaxPrototype))]
public sealed class DeepOceanParallaxTest
{
    [Test]
    public async Task TestDeepOceanIsValidAndIsTheDefault()
    {
        var pair = await PoolManager.GetServerClient();
        try
        {
            await pair.Client.WaitAssertion(() =>
            {
                var protos = pair.Client.ResolveDependency<IPrototypeManager>();
                var res = pair.Client.ResolveDependency<IResourceManager>();

                var deep = protos.Index<ParallaxPrototype>("DeepOcean");
                var def = protos.Index<ParallaxPrototype>("Default");

                Assert.Multiple(() =>
                {
                    // Default must resolve to the same layers, otherwise maps without an
                    // explicit Parallax component silently keep rendering open space.
                    Assert.That(deep.Layers, Is.Not.Empty, "DeepOcean has no layers.");
                    Assert.That(def.Layers, Has.Count.EqualTo(deep.Layers.Count),
                        "Default does not inherit DeepOcean's layers.");
                    Assert.That(def.LayersLQ, Has.Count.EqualTo(deep.LayersLQ.Count),
                        "Default does not inherit DeepOcean's low-quality layers.");
                });

                foreach (var layer in deep.Layers.Concat(deep.LayersLQ))
                {
                    // DeepOcean is entirely image-sourced. A generated layer here would
                    // mean the texture check below silently stopped covering it.
                    Assert.That(layer.Texture, Is.InstanceOf<ImageParallaxTextureSource>(),
                        $"layer uses unexpected texture source {layer.Texture.GetType().Name}");

                    if (layer.Texture is ImageParallaxTextureSource image)
                    {
                        Assert.That(res.TryContentFileRead(image.Path, out _), Is.True,
                            $"Parallax texture does not exist: {image.Path}");
                    }

                    if (!string.IsNullOrEmpty(layer.Shader))
                    {
                        Assert.That(protos.TryIndex<ShaderPrototype>(layer.Shader, out _), Is.True,
                            $"Parallax layer references unknown shader '{layer.Shader}'.");
                    }
                }
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    /// <summary>
    /// Every uniform named in a shader prototype's <c>params</c> block has to
    /// actually be declared by the shader source. The engine only logs a mismatch
    /// and drops the parameter, which means the effect quietly runs at its default
    /// value and looks like an art problem rather than a config problem.
    /// </summary>
    /// <remarks>
    /// The prototype's parsed uniform list isn't publicly reachable, so this pulls
    /// <c>path</c> and <c>params</c> straight out of the prototype YAML and reads the
    /// uniform declarations out of the referenced .swsl by hand.
    /// </remarks>
    [Test]
    public async Task TestShaderParamsMatchShaderUniforms()
    {
        var pair = await PoolManager.GetServerClient();
        try
        {
            // WaitAssertion only takes an Action, so the results are stashed here.
            var problems = new List<string>();

            await pair.Client.WaitAssertion(() =>
            {
                var res = pair.Client.ResolveDependency<IResourceManager>();
                
                foreach (var prototypePath in res.ContentFindFiles(PrototypesPath))
                {
                    if (prototypePath.Extension != "yml")
                        continue;

                    using var fileStream = res.ContentFileRead(prototypePath);
                    var yaml = new YamlStream();
                    yaml.Load(new StreamReader(fileStream));

                    foreach (var (id, shaderPath, parameters) in EnumerateSourceShaders(yaml))
                    {
                        if (shaderPath is null || parameters.Count == 0)
                            continue;

                        var full = new ResPath(shaderPath);
                        if (!res.TryContentFileRead(full, out var shaderStream))
                        {
                            problems.Add($"{id}: shader source {shaderPath} does not exist");
                            continue;
                        }

                        string source;
                        using (shaderStream)
                        using (var reader = new StreamReader(shaderStream, Encoding.UTF8))
                        {
                            source = reader.ReadToEnd();
                        }

                        var uniforms = DeclaredUniforms(source);
                        foreach (var parameter in parameters)
                        {
                            if (!uniforms.Contains(parameter))
                            {
                                problems.Add($"{id}: param '{parameter}' " +
                                    $"is not declared in {shaderPath}");
                            }
                        }
                    }
                }

                });

            Assert.Multiple(() =>
            {
                foreach (var problem in problems)
                    Assert.Fail(problem);
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    /// <summary>
    /// Pulls (id, path, params) out of every <c>type: shader</c> / <c>kind: source</c>
    /// entry in a prototype file.
    /// </summary>
    private static IEnumerable<(string Id, string? Path, HashSet<string> Params)>
        EnumerateSourceShaders(YamlStream yaml)
    {
        foreach (var document in yaml.Documents)
        {
            if (document.RootNode is not YamlSequenceNode root)
                continue;

            foreach (var node in root.Cast<YamlMappingNode>())
            {
                if (!node.TryGetNode("type", out var type) || type.AsString() != "shader")
                    continue;
                if (!node.TryGetNode("kind", out var kind) || kind.AsString() != "source")
                    continue;

                var id = node.TryGetNode("id", out var idNode) ? idNode.AsString() : "<unnamed>";
                var path = node.TryGetNode("path", out var pathNode) ? pathNode.AsString() : null;

                var parameters = new HashSet<string>();
                if (node.TryGetNode("params", out var paramsNode)
                    && paramsNode is YamlMappingNode paramsMapping)
                {
                    foreach (var (key, _) in paramsMapping.Children)
                        parameters.Add(key.AsString());
                }

                yield return (id, path, parameters);
            }
        }
    }

    /// <summary>
    /// Extracts uniform names from a .swsl source. Deliberately simple: it only has
    /// to recognise declarations, and shader bodies never contain the keyword.
    /// </summary>
    private static HashSet<string> DeclaredUniforms(string source)
    {
        var uniforms = new HashSet<string>();

        // Initialised: `uniform highp float amount = 0.5;`
        var initialised = new Regex(@"\buniform\b[^;]*?(\w+)\s*(\[[^\]]*\])?\s*=", RegexOptions.Compiled);
        foreach (Match match in initialised.Matches(source))
            uniforms.Add(match.Groups[1].Value);

        // Uninitialised: `uniform sampler2D TEXTURE;`
        var bare = new Regex(@"\buniform\b[^;]*?(\w+)\s*(\[[^\]]*\])?\s*;", RegexOptions.Compiled);
        foreach (Match match in bare.Matches(source))
            uniforms.Add(match.Groups[1].Value);

        return uniforms;
    }
}