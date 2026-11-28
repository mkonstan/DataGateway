using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using DataGateway.Core.Tests;
using DataGateway.Data;
using DataGateway.Data.Snowflake;
using global::Snowflake.Data.Client;
using SqlKata.Compilers;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Checks the adapter's public boundary, exact package pin and operator documentation.</summary>
/// <remarks>Repository checks use the shared source-root finder; the dependency check reads each project's restore output.</remarks>
public sealed class SnowflakeContractTests
{
    /// <summary>Exposes one engine and keeps the compiler internal without a friend assembly.</summary>
    [Fact]
    public void Adapter_public_boundary()
    {
        var type = typeof(SnowflakeEngine);
        Assert.True(type.IsPublic); Assert.True(type.IsSealed);
        Assert.Equal(typeof(DatabaseEngine<SnowflakeDbConnection>), type.BaseType);
        Assert.True(typeof(IDatabaseEngine).IsAssignableFrom(type));
        Assert.Empty(Assert.Single(type.GetConstructors()).GetParameters());
        Assert.Equal("snowflake", SnowflakeEngine.ProviderName);
        var field = type.GetField("ProviderName")!; Assert.True(field.IsLiteral); Assert.Equal("snowflake", field.GetRawConstantValue());
        Assert.Equal("snowflake", ((IDatabaseEngine)new SnowflakeEngine()).ProviderName);
        Assert.Null(type.GetProperty("ProviderName", BindingFlags.Instance | BindingFlags.Public));
        Assert.Equal(new[] { type }, type.Assembly.GetExportedTypes());
        var compiler = type.Assembly.GetType("DataGateway.Data.Snowflake.SnowflakeCompiler", true)!;
        Assert.True(compiler.IsNotPublic); Assert.True(compiler.IsSealed); Assert.Equal(typeof(PostgresCompiler), compiler.BaseType);
        Assert.Empty(type.Assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>());
    }

    /// <summary>Pins the project, complete generated lock and single solution entry.</summary>
    [Fact]
    public void Adapter_dependency_pin_and_solution()
    {
        var project = XDocument.Load(TestFiles.InRepo("DataGateway.Data.Snowflake/DataGateway.Data.Snowflake.csproj"));
        Assert.Equal("net10.0", Assert.Single(project.Descendants("TargetFramework")).Value);
        Assert.Equal("true", Assert.Single(project.Descendants("RestorePackagesWithLockFile")).Value);
        Assert.Equal("../DataGateway.Data/DataGateway.Data.csproj", (string?)Assert.Single(project.Descendants("ProjectReference")).Attribute("Include"));
        var packages = project.Descendants("PackageReference").ToDictionary(p => (string)p.Attribute("Include")!, p => (string)p.Attribute("Version")!);
        Assert.Equal(2, packages.Count); Assert.Equal("4.0.1", packages["SqlKata"]); Assert.Equal("[6.5.0]", packages["Snowflake.Data"]);
        using var document = JsonDocument.Parse(File.ReadAllText(TestFiles.InRepo("DataGateway.Data.Snowflake/packages.lock.json")));
        var dependencies = document.RootElement.GetProperty("dependencies").GetProperty("net10.0");
        var driver = dependencies.GetProperty("Snowflake.Data");
        Assert.Equal("Direct", driver.GetProperty("type").GetString()); Assert.Equal("[6.5.0, 6.5.0]", driver.GetProperty("requested").GetString());
        Assert.Equal("6.5.0", driver.GetProperty("resolved").GetString()); Assert.Equal("4.0.1", dependencies.GetProperty("SqlKata").GetProperty("resolved").GetString());
        foreach (var item in dependencies.EnumerateObject())
        {
            if (item.Value.GetProperty("type").GetString() == "Project") continue;
            Assert.NotEmpty(item.Value.GetProperty("resolved").GetString()!);
            Assert.Equal(64, Convert.FromBase64String(item.Value.GetProperty("contentHash").GetString()!).Length);
            if (item.Value.TryGetProperty("dependencies", out var children))
                foreach (var child in children.EnumerateObject()) Assert.True(dependencies.TryGetProperty(child.Name, out _), child.Name);
        }
        var solution = XDocument.Load(TestFiles.InRepo("DataGateway.slnx"));
        Assert.Single(solution.Descendants("Project"), p => (string?)p.Attribute("Path") == "DataGateway.Data.Snowflake/DataGateway.Data.Snowflake.csproj");
        foreach (var name in new[] { "DataGateway.Core", "DataGateway.Data", "DataGateway", "DataGateway.Web" })
        {
            Assert.DoesNotContain("Snowflake", File.ReadAllText(TestFiles.InRepo($"{name}/{name}.csproj")), StringComparison.OrdinalIgnoreCase);
            using var assets = JsonDocument.Parse(File.ReadAllText(TestFiles.InRepo($"{name}/obj/project.assets.json")));
            foreach (var item in assets.RootElement.GetProperty("libraries").EnumerateObject()) Assert.DoesNotContain("Snowflake", item.Name, StringComparison.OrdinalIgnoreCase);
            foreach (var target in assets.RootElement.GetProperty("targets").EnumerateObject())
                foreach (var item in target.Value.EnumerateObject()) Assert.DoesNotContain("Snowflake", item.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Recognizes the driver's own exceptions, marking only Snowflake's timeout and cancel codes as timeouts.</summary>
    /// <remarks>An unrecognized driver error would reach callers as UnexpectedError.</remarks>
    [Theory]
    [InlineData(630, true)]
    [InlineData(604, true)]
    [InlineData(2003, false)]
    [InlineData(390189, false)]
    public void Driver_failures_are_recognized(int code, bool timeout)
    {
        var failure = new SnowflakeEngine().RecognizeFailure(new SnowflakeDbException("42000", code, "synthetic", "q"));
        Assert.NotNull(failure);
        Assert.Equal(timeout, failure.IsTimeout);
        Assert.Equal(code, failure.Number);
        Assert.Null(new SnowflakeEngine().RecognizeFailure(new InvalidOperationException("other")));
    }

    /// <summary>Documents exact stored spelling, the session precondition, naming and comparison rules.</summary>
    [Fact]
    public void Operator_documentation_and_local_implementation()
    {
        var readme = File.ReadAllText(TestFiles.InRepo("DataGateway.Data.Snowflake/README.md"));
        Assert.Contains("QUOTED_IDENTIFIERS_IGNORE_CASE = FALSE", readme);
        Assert.Contains("stored spelling", readme); Assert.Contains("`schema.view`", readme);
        Assert.Contains("case-sensitive", readme); Assert.Contains("SourceUnavailable", readme);
        var engine = File.ReadAllText(TestFiles.InRepo("DataGateway.Data.Snowflake/SnowflakeEngine.cs"));
        var compiler = File.ReadAllText(TestFiles.InRepo("DataGateway.Data.Snowflake/SnowflakeCompiler.cs"));
        Assert.DoesNotContain("System.Reflection", engine + compiler); Assert.DoesNotContain("ReadSourceAsync", engine);
        Assert.DoesNotContain("InternalsVisibleTo", engine + compiler);
        Assert.Contains("SnowflakeDbFactory.Instance.CreateConnectionStringBuilder()", engine);
    }
}
