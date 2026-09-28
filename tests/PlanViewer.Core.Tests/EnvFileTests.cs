using PlanViewer.Cli;

namespace PlanViewer.Core.Tests;

/// <summary>
/// A .env file in the working directory can pick the server and turn off certificate validation.
/// The CLI names the file and the settings it supplied on stderr, and never their values.
///
/// <para>Shares a collection with <see cref="CliConnectionResolverTests"/> because one test here swaps
/// Console.Error, as that class does. An uncaptured stderr write in this test host has aborted a whole
/// run before, so the two swaps must not interleave.</para>
/// </summary>
[Collection("EntraInteractiveAuth process-wide state")]
public class EnvFileTests : IDisposable
{
    private readonly List<string> _directories = [];

    [Fact]
    public void Notice_NamesTheFileAndOnlyTheSettingsItSupplied()
    {
        var env = Load("""
            # comment
            PLANVIEW_SERVER=elsewhere
            PLANVIEW_DATABASE='db'
            PLANVIEW_TRUST_CERT=TRUE
            """);

        // The command had --server, so it never asks the file for PLANVIEW_SERVER.
        Assert.Equal("db", env.Use("PLANVIEW_DATABASE"));
        Assert.True(env.UseFlag("PLANVIEW_TRUST_CERT"));

        Assert.Equal($"Using settings from {env.FilePath}: PLANVIEW_DATABASE, PLANVIEW_TRUST_CERT", env.Notice);
        Assert.True(Path.IsPathFullyQualified(env.FilePath!));
    }

    [Fact]
    public void Notice_NeverShowsAValue()
    {
        var env = Load("PLANVIEW_PASSWORD=\"s3cret\"");

        Assert.Equal("s3cret", env.Use("PLANVIEW_PASSWORD"));
        Assert.DoesNotContain("s3cret", env.Notice);
        Assert.EndsWith(": PLANVIEW_PASSWORD", env.Notice);
    }

    [Fact]
    public void TrustCertOtherThanTrue_ChangesNothingAndIsNotReported()
    {
        var env = Load("PLANVIEW_TRUST_CERT=false");

        Assert.False(env.UseFlag("PLANVIEW_TRUST_CERT"));
        Assert.Null(env.Notice);
    }

    [Fact]
    public void NoEnvFile_SuppliesNothing()
    {
        var env = ConnectionHelper.LoadEnvFile(NewDirectory());

        Assert.Null(env.FilePath);
        Assert.Null(env.Use("PLANVIEW_SERVER"));
        Assert.Null(env.Notice);
    }

    [Fact]
    public void PasswordFromTheFile_IsReportedWhenNoOptionGaveOne()
    {
        var env = Load("PLANVIEW_PASSWORD=fromfile");

        Assert.True(PasswordResolver.TryResolve(null, false, false, () => env.Use("PLANVIEW_PASSWORD"), out var password));

        Assert.Equal("fromfile", password);
        Assert.EndsWith(": PLANVIEW_PASSWORD", env.Notice);
    }

    [Fact]
    public void PasswordFromTheFile_IsNotAskedForWhenAnOptionGaveOne()
    {
        var env = Load("PLANVIEW_PASSWORD=fromfile");

        /* --password writes its process-listing warning to stderr, so capture the stream for the call. */
        var realError = Console.Error;
        Console.SetError(new StringWriter());
        try
        {
            Assert.True(PasswordResolver.TryResolve("inline", false, false, () => env.Use("PLANVIEW_PASSWORD"), out var password));
            Assert.Equal("inline", password);
        }
        finally
        {
            Console.SetError(realError);
        }

        Assert.Null(env.Notice);
    }

    private EnvFile Load(string contents)
    {
        var directory = NewDirectory();
        File.WriteAllText(Path.Combine(directory, ".env"), contents);
        return ConnectionHelper.LoadEnvFile(directory);
    }

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "planview-envfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
            Directory.Delete(directory, recursive: true);
    }
}
