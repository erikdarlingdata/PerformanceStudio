using PlanViewer.Cli;

namespace PlanViewer.Core.Tests;

/// <summary>
/// A .env file in the working directory can pick the server and turn off certificate validation.
/// The CLI takes from the file only what the command line left out, names the file and the settings
/// it supplied on stderr, and never shows their values.
/// </summary>
public class EnvFileTests : IDisposable
{
    private const char Esc = (char)0x1B;

    private const string AllSettings = """
        # comment
        PLANVIEW_SERVER=srv-in-file
        PLANVIEW_DATABASE='db-in-file'
        PLANVIEW_LOGIN="login-in-file"
        PLANVIEW_TRUST_CERT=TRUE
        PLANVIEW_PASSWORD=pw-in-file
        """;

    private static readonly ConnectionSettings EmptyCommandLine = new(null, null, null, false);

    private readonly List<string> _directories = [];

    [Fact]
    public void EverySettingFromTheFile_FillsTheGapsAndIsListedInOrder()
    {
        var env = Load(AllSettings);

        var settings = env.Fill(EmptyCommandLine);

        Assert.Equal(new ConnectionSettings("srv-in-file", "db-in-file", "login-in-file", true), settings);
        Assert.Equal("pw-in-file", env.PasswordFor(settings));
        Assert.Equal(
            $"Using settings from {env.FilePath}: PLANVIEW_SERVER, PLANVIEW_DATABASE, PLANVIEW_LOGIN, PLANVIEW_TRUST_CERT, PLANVIEW_PASSWORD",
            env.Notice);
        Assert.True(Path.IsPathFullyQualified(env.FilePath!));
    }

    [Theory]
    [InlineData("PLANVIEW_SERVER")]
    [InlineData("PLANVIEW_DATABASE")]
    [InlineData("PLANVIEW_LOGIN")]
    [InlineData("PLANVIEW_TRUST_CERT")]
    public void AnOptionOnTheCommandLine_WinsAndItsKeyIsNotListed(string key)
    {
        var env = Load(AllSettings);
        var commandLine = key switch
        {
            "PLANVIEW_SERVER" => EmptyCommandLine with { Server = "cli-server" },
            "PLANVIEW_DATABASE" => EmptyCommandLine with { Database = "cli-db" },
            "PLANVIEW_LOGIN" => EmptyCommandLine with { Login = "cli-login" },
            _ => EmptyCommandLine with { TrustCert = true },
        };

        var settings = env.Fill(commandLine);

        Assert.Equal(
            new ConnectionSettings(
                commandLine.Server ?? "srv-in-file",
                commandLine.Database ?? "db-in-file",
                commandLine.Login ?? "login-in-file",
                true),
            settings);

        string[] keys = ["PLANVIEW_SERVER", "PLANVIEW_DATABASE", "PLANVIEW_LOGIN", "PLANVIEW_TRUST_CERT"];
        Assert.Equal($"Using settings from {env.FilePath}: {string.Join(", ", keys.Where(k => k != key))}", env.Notice);
    }

    [Fact]
    public void NoServer_TakesNothingFromTheFile()
    {
        var env = Load("""
            PLANVIEW_DATABASE=db-in-file
            PLANVIEW_LOGIN=login-in-file
            PLANVIEW_TRUST_CERT=true
            PLANVIEW_PASSWORD=pw-in-file
            """);

        var settings = env.Fill(EmptyCommandLine);

        // With no server, analyze reads a plan file offline, so none of these would have an effect.
        Assert.Equal(EmptyCommandLine, settings);
        Assert.Null(env.PasswordFor(settings));
        Assert.Null(env.Notice);
    }

    [Fact]
    public void PasswordFromTheFile_IsUsedOnlyWithALogin()
    {
        var env = Load("""
            PLANVIEW_SERVER=srv-in-file
            PLANVIEW_PASSWORD=pw-in-file
            """);

        // Without a login the command uses the credential store or Windows authentication.
        var settings = env.Fill(EmptyCommandLine);
        Assert.Null(env.PasswordFor(settings));
        Assert.Null(env.PasswordFor(settings with { Login = "" }));
        Assert.Equal($"Using settings from {env.FilePath}: PLANVIEW_SERVER", env.Notice);

        Assert.Equal("pw-in-file", env.PasswordFor(settings with { Login = "cli-login" }));
        Assert.Equal($"Using settings from {env.FilePath}: PLANVIEW_SERVER, PLANVIEW_PASSWORD", env.Notice);
    }

    [Fact]
    public void PasswordFromTheFile_IsListedWhenNoOptionGaveOne()
    {
        var env = Load(AllSettings);
        var settings = env.Fill(EmptyCommandLine);
        var error = new StringWriter();

        Assert.True(PasswordResolver.TryResolve(null, false, false, () => env.PasswordFor(settings), out var password, error));

        Assert.Equal("pw-in-file", password);
        Assert.EndsWith(", PLANVIEW_PASSWORD", env.Notice);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void PasswordFromTheFile_IsNotAskedForWhenAnOptionGaveOne()
    {
        var env = Load(AllSettings);
        var settings = env.Fill(EmptyCommandLine);
        var error = new StringWriter();

        Assert.True(PasswordResolver.TryResolve("inline", false, false, () => env.PasswordFor(settings), out var password, error));

        Assert.Equal("inline", password);
        Assert.DoesNotContain("PLANVIEW_PASSWORD", env.Notice);
        Assert.Contains("--password is visible", error.ToString());
    }

    [Fact]
    public void Notice_NeverShowsAValue()
    {
        var env = Load(AllSettings);

        env.PasswordFor(env.Fill(EmptyCommandLine));

        Assert.NotNull(env.Notice);
        foreach (var value in new[] { "srv-in-file", "db-in-file", "login-in-file", "pw-in-file" })
            Assert.DoesNotContain(value, env.Notice);
    }

    [Fact]
    public void TrustCertOtherThanTrue_ChangesNothingAndIsNotListed()
    {
        var env = Load("""
            PLANVIEW_SERVER=srv-in-file
            PLANVIEW_TRUST_CERT=false
            """);

        Assert.False(env.Fill(EmptyCommandLine).TrustCert);
        Assert.Equal($"Using settings from {env.FilePath}: PLANVIEW_SERVER", env.Notice);
    }

    [Fact]
    public void NoEnvFile_SuppliesNothing()
    {
        var env = ConnectionHelper.LoadEnvFile(NewDirectory());
        var commandLine = EmptyCommandLine with { Server = "cli-server", Login = "cli-login" };

        var settings = env.Fill(commandLine);

        Assert.Null(env.FilePath);
        Assert.Null(env.Error);
        Assert.Equal(commandLine, settings);
        Assert.Null(env.PasswordFor(settings));
        Assert.Null(env.Notice);
    }

    [Theory]
    [InlineData(0x1B)] // ESC, which starts the sequences that move the cursor and erase lines
    [InlineData(0x9B)] // CSI, the one-character form of ESC [
    [InlineData(0x07)]
    [InlineData(0x00)]
    [InlineData(0x7F)]
    public void ControlCharacterInAValue_StopsTheCommandWithoutShowingTheValue(int code)
    {
        var env = Load($"""
            PLANVIEW_SERVER=srv-in-file
            PLANVIEW_DATABASE=db-in-file{(char)code}[1A
            """);

        Assert.NotNull(env.Error);
        Assert.Contains("PLANVIEW_DATABASE", env.Error);
        Assert.Contains(env.FilePath!, env.Error);
        Assert.DoesNotContain("db-in-file", env.Error);
        Assert.DoesNotContain(env.Error, c => char.IsControl(c));
    }

    [Fact]
    public void Tab_IsAllowed()
    {
        var env = Load($"PLANVIEW_DATABASE=db{(char)0x09}in{(char)0x09}file");

        Assert.Null(env.Error);
    }

    [Fact]
    public void ControlCharacterOutsideThePlanviewSettings_IsIgnored()
    {
        // A .env file often holds settings for other tools too.
        var env = Load($"""
            OTHER_TOOL_BANNER=hello{Esc}[1m
            PLANVIEW_SERVER=srv-in-file
            """);

        Assert.Null(env.Error);
        Assert.Equal("srv-in-file", env.Fill(EmptyCommandLine).Server);
    }

    [Fact]
    public void ControlCharacterInThePath_IsShownAsAQuestionMark()
    {
        // Windows does not allow ESC in a directory name, so build the EnvFile directly.
        var path = Path.Combine(Path.GetTempPath(), $"bad{Esc}[2Kdir", ".env");

        var env = new EnvFile(path, new(StringComparer.OrdinalIgnoreCase) { ["PLANVIEW_SERVER"] = "srv-in-file" });
        env.Fill(EmptyCommandLine);

        Assert.Contains("bad?[2Kdir", env.Notice);
        Assert.DoesNotContain(env.Notice!, c => char.IsControl(c));

        var broken = new EnvFile(path, new(StringComparer.OrdinalIgnoreCase) { ["PLANVIEW_SERVER"] = $"srv{Esc}" });

        Assert.Contains("bad?[2Kdir", broken.Error);
        Assert.DoesNotContain(broken.Error!, c => char.IsControl(c));
    }

    private EnvFile Load(string contents)
    {
        var directory = NewDirectory();
        File.WriteAllText(Path.Combine(directory, ".env"), contents);
        return ConnectionHelper.LoadEnvFile(directory);
    }

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "planview-envtest-" + Guid.NewGuid().ToString("N"));
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
