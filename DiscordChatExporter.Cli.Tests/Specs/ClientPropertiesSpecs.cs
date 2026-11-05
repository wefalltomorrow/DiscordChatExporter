using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Core.Discord;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ClientPropertiesSpecs
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(0, false)]
    public async Task Cli_diagnostics_use_stderr_and_survive_failure_and_cancellation(
        int outcome,
        bool diagnostics
    )
    {
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        using var console = new FakeConsole(Stream.Null, output, error);
        using var command = new DiagnosticTestCommand(outcome)
        {
            Token = "private-token",
            ShowDiagnostics = diagnostics,
        };
        var act = async () => await command.ExecuteAsync(console);
        if (outcome == 1)
            await act.Should().ThrowAsync<InvalidOperationException>();
        else if (outcome == 2)
            await act.Should().ThrowAsync<OperationCanceledException>();
        else
            await act.Should().NotThrowAsync();
        console.Error.Flush();
        console.Output.Flush();
        var text = Encoding.UTF8.GetString(error.ToArray());
        text.Split("Core version:").Length.Should().Be(diagnostics ? 3 : 1);
        text.Should().NotContain("private-token");
        Encoding.UTF8.GetString(output.ToArray()).Should().NotContain("Core version:");
    }

    private sealed class DiagnosticTestCommand(int outcome) : DiscordCommandBase, IDisposable
    {
        protected override ValueTask ExecuteCoreAsync(IConsole console) =>
            outcome switch
            {
                1 => throw new InvalidOperationException("Test failure"),
                2 => throw new OperationCanceledException(),
                _ => ValueTask.CompletedTask,
            };

        public void Dispose() => Discord.Dispose();
    }

    [Theory]
    [InlineData(false, null, "supported only on Windows")]
    [InlineData(true, "true", "disabled by the environment")]
    public void Disabled_transport_does_not_initialize_native_session(
        bool windows,
        string? disabled,
        string reason
    )
    {
        var called = false;
        var result = DiscordClient.InitializeBrowserTransport(
            windows,
            disabled,
            () =>
            {
                called = true;
                return new object();
            }
        );
        called.Should().BeFalse();
        result.Session.Should().BeNull();
        result.Reason.Should().Contain(reason);
    }

    [Fact]
    public void Native_initialization_failure_reports_type_without_private_exception_text()
    {
        var result = DiscordClient.InitializeBrowserTransport<object>(
            true,
            null,
            () => throw new DllNotFoundException("private-path-and-token")
        );
        result.Session.Should().BeNull();
        result
            .Reason.Should()
            .Contain(nameof(DllNotFoundException))
            .And.NotContain("private-path-and-token");
    }

    [Fact]
    public void Successful_native_initialization_retains_session()
    {
        var session = new object();
        var result = DiscordClient.InitializeBrowserTransport(true, null, () => session);
        result.Session.Should().BeSameAs(session);
        result.Reason.Should().Contain("successfully");
    }

    [Fact]
    public void I_can_encode_official_web_super_properties_with_the_expected_shape()
    {
        const string launchId = "00000000-0000-4000-8000-000000000001";
        const string launchSignature = "00000000-0000-4000-8000-000000000002";
        const string heartbeatId = "00000000-0000-4000-8000-000000000003";

        var encoded = DiscordUserClientProfile.EncodeXSuperProperties(
            DiscordUserClientProfile.FallbackClientBuildNumber,
            launchId,
            launchSignature,
            heartbeatId
        );

        using var doc = JsonDocument.Parse(Convert.FromBase64String(encoded));
        var root = doc.RootElement;

        root.EnumerateObject()
            .Select(p => p.Name)
            .Should()
            .Equal(
                "os",
                "browser",
                "device",
                "system_locale",
                "has_client_mods",
                "browser_user_agent",
                "browser_version",
                "os_version",
                "referrer",
                "referring_domain",
                "referrer_current",
                "referring_domain_current",
                "release_channel",
                "client_build_number",
                "client_event_source",
                "client_launch_id",
                "launch_signature",
                "client_heartbeat_session_id",
                "client_app_state"
            );

        root.GetProperty("os").GetString().Should().Be("Windows");
        root.GetProperty("browser").GetString().Should().Be("Chrome");
        root.GetProperty("device").GetString().Should().BeEmpty();
        root.GetProperty("system_locale").GetString().Should().Be("en-US");
        root.GetProperty("has_client_mods").GetBoolean().Should().BeFalse();
        root.GetProperty("browser_user_agent")
            .GetString()
            .Should()
            .Be(DiscordUserClientProfile.BrowserUserAgent);
        root.GetProperty("browser_version")
            .GetString()
            .Should()
            .Be($"{DiscordUserClientProfile.BrowserMajorVersion}.0.0.0");
        root.GetProperty("os_version").GetString().Should().Be("10");
        root.GetProperty("release_channel").GetString().Should().Be("stable");
        root.GetProperty("client_build_number").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("client_build_number")
            .GetInt32()
            .Should()
            .Be(DiscordUserClientProfile.FallbackClientBuildNumber);
        root.GetProperty("client_event_source").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("client_launch_id").GetString().Should().Be(launchId);
        root.GetProperty("launch_signature").GetString().Should().Be(launchSignature);
        root.GetProperty("client_heartbeat_session_id").GetString().Should().Be(heartbeatId);
        root.GetProperty("client_app_state").GetString().Should().Be("unfocused");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", true)]
    [InlineData("false", true)]
    [InlineData("1", false)]
    [InlineData("true", false)]
    [InlineData("yes", false)]
    [InlineData("on", false)]
    public void Browser_transport_can_be_disabled_explicitly(string? disabledValue, bool expected)
    {
        DiscordUserClientProfile.BrowserMajorVersion.Should().BePositive();

        DiscordClient
            .ShouldUseBrowserTransport(disabledValue, isWindows: true)
            .Should()
            .Be(expected);
    }

    [Fact]
    public void Browser_transport_is_never_enabled_on_non_windows_hosts()
    {
        DiscordClient.ShouldUseBrowserTransport(null, isWindows: false).Should().BeFalse();
    }

    [Fact]
    public void Browser_major_version_is_consistent_with_user_agent()
    {
        DiscordUserClientProfile
            .BrowserUserAgent.Should()
            .Contain($"Chrome/{DiscordUserClientProfile.BrowserMajorVersion}.");
    }

    [Fact]
    public void User_agent_matches_the_super_properties_browser_user_agent()
    {
        var encoded = DiscordUserClientProfile.EncodeXSuperProperties(
            DiscordUserClientProfile.FallbackClientBuildNumber,
            Guid.NewGuid().ToString(),
            DiscordUserClientProfile.GenerateLaunchSignature(),
            Guid.NewGuid().ToString()
        );

        using var doc = JsonDocument.Parse(Convert.FromBase64String(encoded));

        doc.RootElement.GetProperty("browser_user_agent")
            .GetString()
            .Should()
            .Be(DiscordUserClientProfile.BrowserUserAgent);
    }

    [Fact]
    public void Launch_signature_clears_client_mod_detection_bits()
    {
        var signatures = Enumerable
            .Range(0, 32)
            .Select(_ => DiscordUserClientProfile.GenerateLaunchSignature())
            .ToArray();

        Span<byte> bytes = stackalloc byte[16];
        foreach (var signature in signatures)
        {
            var guid = Guid.Parse(signature);
            guid.TryWriteBytes(bytes, bigEndian: true, out _);

            var value = new UInt128(
                BinaryPrimitives.ReadUInt64BigEndian(bytes),
                BinaryPrimitives.ReadUInt64BigEndian(bytes[8..])
            );

            (value & DiscordUserClientProfile.LaunchSignatureMask).Should().Be(UInt128.Zero);
            guid.ToString().Should().Be(signature);
        }
    }

    [Fact]
    public void I_can_parse_the_client_build_number_from_discord_app_html()
    {
        const string html =
            """<script>window.GLOBAL_ENV = {"NODE_ENV":"production","BUILD_NUMBER":"594503","RELEASE_CHANNEL":"stable"}</script>""";

        var buildNumber = DiscordUserClientProfile.TryParseClientBuildNumber(html);

        buildNumber.Should().Be(594503);
        DiscordUserClientProfile.TryParseClientBuildNumber("<html></html>").Should().BeNull();
        DiscordUserClientProfile
            .TryParseClientBuildNumber("\"BUILD_NUMBER\":\"abc\"")
            .Should()
            .BeNull();
    }
}
