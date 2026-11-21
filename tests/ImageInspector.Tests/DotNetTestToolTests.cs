using System.Reflection;
using System.Text.Json;
using oaiResponsesAvalonia;
using oaiResponsesAvalonia.Services;

namespace ImageInspector.Tests;

public sealed class DotNetTestToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Dispatcher_RunsTestsAndReportsFailures()
    {
        File.WriteAllText(Path.Combine(_root, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>
            """);
        string testFile = Path.Combine(_root, "SampleTests.cs");
        File.WriteAllText(testFile, "public class SampleTests { [Xunit.Fact] public void Sample() { Xunit.Assert.True(true); } }");

        var options = new DeveloperToolsOptions { RepositoryRoot = _root, ReadOnlyOnly = false };
        var guard = new WorkspaceGuard(options);
        var dispatcher = new LocalToolDispatcher(
            new ProjectFileToolService(options, guard),
            new ProjectSearchToolService(options, guard),
            new DotNetProjectToolService(options, guard, new DotNetCliService(), new DeveloperProcessManager()));

        using JsonDocument success = JsonDocument.Parse(await dispatcher.DispatchAsync(
            "test_dotnet_project", """{"target_path":"Sample.csproj","configuration":"Release"}"""));
        Assert.True(success.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("test_dotnet_project", success.RootElement.GetProperty("operation").GetString());
        Assert.Equal("Release", success.RootElement.GetProperty("configuration").GetString());
        Assert.Contains("Passed", success.RootElement.GetProperty("standard_output").GetString());

        File.WriteAllText(testFile, "public class SampleTests { [Xunit.Fact] public void Sample() { Xunit.Assert.True(false); } }");
        using JsonDocument failure = JsonDocument.Parse(await dispatcher.DispatchAsync(
            "test_dotnet_project", """{"target_path":"Sample.csproj"}"""));
        Assert.False(failure.RootElement.GetProperty("ok").GetBoolean());
        Assert.NotEqual(0, failure.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal("Debug", failure.RootElement.GetProperty("configuration").GetString());
    }

    [Fact]
    public async Task TestTool_RejectsDisabledAndInvalidRequests()
    {
        File.WriteAllText(Path.Combine(_root, "Sample.csproj"), "<Project />");
        var options = new DeveloperToolsOptions { RepositoryRoot = _root, ReadOnlyOnly = true };
        var service = new DotNetProjectToolService(options, new WorkspaceGuard(options), new DotNetCliService(), new DeveloperProcessManager());

        async Task AssertError(string path, string configuration, string expected)
        {
            using JsonDocument result = JsonDocument.Parse(await service.TestDotNetProjectAsync(path, configuration));
            Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains(expected, result.RootElement.GetProperty("error").GetString());
        }

        await AssertError("Sample.csproj", "", "read-only");
        options.ReadOnlyOnly = false;
        options.TestDotNetProjectEnabled = false;
        await AssertError("Sample.csproj", "", "not enabled");
        options.TestDotNetProjectEnabled = true;
        await AssertError("../outside.csproj", "", "escapes");
        await AssertError("sample.dll", "", "requires a .csproj");
        await AssertError("missing.csproj", "", "not found");
        await AssertError("Sample.csproj", "Other", "Debug or Release");
    }

    [Fact]
    public void TestTool_IsAdvertisedOnlyWhenEnabledAndRequiresExecutionConfirmation()
    {
        using var responses = new Responses("test");
        var options = new DeveloperToolsOptions { Enabled = true, ReadOnlyOnly = false };
        MethodInfo getTools = typeof(Responses).GetMethod("GetLocalFunctionTools", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo isExecution = typeof(Responses).GetMethod("IsExecutionDeveloperTool", BindingFlags.Static | BindingFlags.NonPublic)!;

        bool IsAdvertised() => JsonSerializer.Serialize((object[])getTools.Invoke(responses, [options])!).Contains("\"name\":\"test_dotnet_project\"");

        Assert.True(IsAdvertised());
        Assert.True((bool)isExecution.Invoke(null, ["test_dotnet_project"])!);
        options.ReadOnlyOnly = true;
        Assert.False(IsAdvertised());
        options.ReadOnlyOnly = false;
        options.TestDotNetProjectEnabled = false;
        Assert.False(IsAdvertised());
    }
}
