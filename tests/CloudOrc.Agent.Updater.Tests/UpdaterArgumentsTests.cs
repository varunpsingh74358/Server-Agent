using CloudOrc.Agent.Updater;

namespace CloudOrc.Agent.Updater.Tests;

public class UpdaterArgumentsTests
{
    private static readonly string Sha = new('a', 64);

    private static List<string> ValidArgs() =>
    [
        "--installer", @"C:\Data\updates\setup.exe",
        "--sha256", Sha,
        "--expected-version", "1.4.0",
        "--install-dir", @"C:\Program Files\CloudOrc\Agents",
        "--work-dir", @"C:\ProgramData\CloudOrc\ControlAgent\updates",
        "--result-file", @"C:\ProgramData\CloudOrc\ControlAgent\updates\result-u1.json",
        "--update-id", "u1"
    ];

    [Fact]
    public void Parses_valid_arguments_with_defaults()
    {
        var parsed = UpdaterArguments.TryParse(ValidArgs().ToArray(), out var error, out _);

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("1.4.0", parsed!.ExpectedVersion);
        Assert.Equal(UpdaterArguments.DefaultHealthTimeoutSeconds, parsed.HealthTimeoutSeconds);
        Assert.False(parsed.AllowUnsigned);
        Assert.Equal(@"C:\ProgramData\CloudOrc\ControlAgent\updates\rollback\u1", parsed.RollbackDirectory);
        Assert.Equal(@"C:\Program Files\CloudOrc\Agents\ControlAgent\CloudOrc.ControlAgent.exe", parsed.ControlAgentExe);
    }

    [Fact]
    public void Accepts_equals_syntax_and_optional_flags()
    {
        var args = ValidArgs();
        args.Add("--health-timeout-seconds=60");
        args.Add("--allow-unsigned");

        var parsed = UpdaterArguments.TryParse(args.ToArray(), out var error, out _);

        Assert.Null(error);
        Assert.Equal(60, parsed!.HealthTimeoutSeconds);
        Assert.True(parsed.AllowUnsigned);
    }

    [Theory]
    [InlineData("--installer")]
    [InlineData("--sha256")]
    [InlineData("--expected-version")]
    [InlineData("--install-dir")]
    [InlineData("--work-dir")]
    [InlineData("--result-file")]
    [InlineData("--update-id")]
    public void Missing_required_option_is_rejected(string option)
    {
        var args = ValidArgs();
        var index = args.IndexOf(option);
        args.RemoveRange(index, 2);

        var parsed = UpdaterArguments.TryParse(args.ToArray(), out var error, out _);

        Assert.Null(parsed);
        Assert.Contains(option.TrimStart('-'), error);
    }

    [Theory]
    [InlineData("--sha256", "abc")]
    [InlineData("--sha256", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("--expected-version", "latest")]
    [InlineData("--update-id", "../../evil")]
    [InlineData("--health-timeout-seconds", "5")]
    [InlineData("--health-timeout-seconds", "abc")]
    public void Invalid_values_are_rejected(string option, string value)
    {
        var args = ValidArgs();
        var index = args.IndexOf(option);
        if (index >= 0)
        {
            args[index + 1] = value;
        }
        else
        {
            args.Add(option);
            args.Add(value);
        }

        Assert.Null(UpdaterArguments.TryParse(args.ToArray(), out var error, out _));
        Assert.NotNull(error);
    }

    [Fact]
    public void Unknown_option_and_missing_value_are_rejected()
    {
        var unknown = ValidArgs();
        unknown.Add("--bogus");
        unknown.Add("x");
        Assert.Null(UpdaterArguments.TryParse(unknown.ToArray(), out var e1, out _));
        Assert.Contains("Unknown option", e1);

        var missingValue = ValidArgs();
        missingValue.Insert(0, "--installer");
        Assert.Null(UpdaterArguments.TryParse(missingValue.ToArray(), out var e2, out _));
        Assert.NotNull(e2);
    }

    [Fact]
    public void Work_dir_inside_install_dir_is_rejected()
    {
        var args = ValidArgs();
        args[args.IndexOf("--work-dir") + 1] = @"C:\Program Files\CloudOrc\Agents\work";

        Assert.Null(UpdaterArguments.TryParse(args.ToArray(), out var error, out _));
        Assert.Contains("work-dir", error);
    }

    [Fact]
    public void Result_file_hint_is_returned_even_when_parse_fails()
    {
        var args = ValidArgs();
        args[args.IndexOf("--sha256") + 1] = "bad";

        Assert.Null(UpdaterArguments.TryParse(args.ToArray(), out _, out var hint));
        Assert.Equal(@"C:\ProgramData\CloudOrc\ControlAgent\updates\result-u1.json", hint);
    }
}
