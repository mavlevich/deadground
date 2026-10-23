using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class ArchitectureBuildTests
{
    [Fact]
    public async Task BuildRejectsGodotThroughATransitiveProjectAndADirectAssemblyReference()
    {
        using Fixture fixture = new();
        string stub = fixture.Project("Stub", "<AssemblyName>GodotSharp</AssemblyName>");
        fixture.Project("Bridge", references: Reference("ProjectReference", "../Stub/Stub.csproj"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(stub)!, "Marker.cs"), "namespace GodotStub; public class Marker { }");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(stub)!, "..", "Bridge", "UsesGodot.cs"),
            "public class UsesGodot { public GodotStub.Marker Value = new(); }");
        string consumer = fixture.Project("Game.Rules.Invalid", references: Reference("ProjectReference", "../Bridge/Bridge.csproj"), guarded: true);
        (int code, string output) = await Build(consumer);
        Assert.True(code != 0, output);
        Assert.Contains("Only Game.Godot may reference Godot assemblies", output);
        Assert.Contains("GodotSharp.dll", output);

        string direct = fixture.Project("Game.Server.Invalid", references:
            new XElement("Reference", new XAttribute("Include", "GodotSharp"),
                new XElement("HintPath", Path.Combine(Path.GetDirectoryName(stub)!, "bin", "Debug", "net10.0", "GodotSharp.dll"))).ToString(), guarded: true);
        (code, output) = await Build(direct);
        Assert.NotEqual(0, code);
        Assert.Contains("Only Game.Godot may reference Godot assemblies", output);
    }

    [Fact]
    public async Task BuildRejectsTheGodotSdkEvenWithItsImplicitReferencesDisabled()
    {
        using Fixture fixture = new();
        string project = fixture.Project("Game.Rules.InvalidSdk", sdk: fixture.GodotSdk, guarded: true);
        (int code, string output) = await Build(project, "-target:CheckArchitecture",
            "-property:DisableImplicitGodotSharpReferences=true", "-property:DisableImplicitGodotGeneratorReferences=true");
        Assert.NotEqual(0, code);
        Assert.Contains("Only Game.Godot may use the Godot SDK", output);
    }

    private static string Reference(string kind, string include) =>
        new XElement(kind, new XAttribute("Include", include)).ToString();

    private static async Task<(int Code, string Output)> Build(string project, params string[] extra)
    {
        ProcessStartInfo info = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "build", project, "--nologo", "-nodeReuse:false", "-maxcpucount:1" }) info.ArgumentList.Add(arg);
        foreach (string arg in extra) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "deadground architecture " + Guid.NewGuid().ToString("N"));
        private readonly string _targets;
        internal string GodotSdk { get; }

        internal Fixture()
        {
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "DeadGround.sln"))) root = root.Parent;
            _targets = Path.Combine(root!.FullName, "Directory.Build.targets");
            GodotSdk = XDocument.Load(Path.Combine(root.FullName, "src", "Game.Godot", "Game.Godot.csproj"))
                .Root!.Attribute("Sdk")!.Value;
            Directory.CreateDirectory(_path);
            File.Copy(Path.Combine(root.FullName, "global.json"), Path.Combine(_path, "global.json"));
        }

        internal string Project(string name, string properties = "", string references = "", bool guarded = false,
            string sdk = "Microsoft.NET.Sdk")
        {
            string directory = Path.Combine(_path, name);
            Directory.CreateDirectory(directory);
            XElement xml = new("Project", new XAttribute("Sdk", sdk),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0")), new XElement("ItemGroup"));
            xml.Element("PropertyGroup")!.Add(XElement.Parse("<Extra>" + properties + "</Extra>").Elements());
            xml.Element("ItemGroup")!.Add(XElement.Parse("<Extra>" + references + "</Extra>").Elements());
            if (guarded) xml.Add(new XElement("Import", new XAttribute("Project", _targets)));
            string project = Path.Combine(directory, name + ".csproj");
            File.WriteAllText(project, xml.ToString());
            return project;
        }

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
