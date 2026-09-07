using System.IO;
using OpenAorus.App;
using OpenAorus.App.Hotkeys;
using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The Settings card's half of the hotkey feature: what the five switches and the duration do
/// when the owner presses Save.
/// </summary>
/// <remarks>
/// <see cref="HotkeyPolicyTests"/> proves what the settings mean; this proves the owner can
/// actually reach them, that a saved file survives a restart, and that a duration typed outside
/// the range the app accepts is corrected in front of the owner rather than behind them.
/// </remarks>
public class HotkeysViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    /// <summary>Everything the card asked the dialog's status line to say, in order.</summary>
    private readonly List<string> _reported = new();

    private HotkeysViewModel Card(AppServices services) => new(services, _reported.Add);

    /// <summary>The last thing the card said, or "" if it has said nothing.</summary>
    private string LastMessage => _reported.Count == 0 ? "" : _reported[^1];

    private AppServices Build(AppSettings settings, bool listening = false)
    {
        var profile = ModelProfile.Detect("AORUS 17G KD");
        var wmi = new FakeGigabyteWmi();
        var hid = new FakeKeyboardHid();
        return new AppServices
        {
            Profile = profile,
            Wmi = wmi,
            Fans = new FanController(wmi, profile, delay: _ => Task.CompletedTask),
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi),
            Store = new SettingsStore(Path.Combine(_dir, Guid.NewGuid().ToString("N"), "settings.json")),
            Settings = settings,
            Gcc = new FakeGccSystem(),
            Lighting = new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUk), _ => Task.CompletedTask),
            KeyboardPresent = hid.IsPresent,
            Version = "0.0.0",
            ExePath = "OpenAorus.Tests.exe",
            Hotkeys = listening
                ? new HotkeyService(new FakeHotkeySource(), new FakeWmiEventSource(), settings.Hotkeys,
                    () => FanMode.Normal)
                : null,
        };
    }

    [Fact]
    public void The_card_opens_showing_what_is_saved()
    {
        var settings = new AppSettings();
        settings.Hotkeys.Enabled = false;
        settings.Hotkeys.OverlayForBacklight = true;
        settings.Hotkeys.OverlayForWifi = true;
        settings.Hotkeys.OverlaySeconds = 4;

        var vm = Card(Build(settings));

        Assert.False(vm.HotkeysEnabled);
        Assert.False(vm.OverlayForFanMode);
        Assert.True(vm.OverlayForBacklight);
        Assert.False(vm.OverlayForTouchpad);
        Assert.True(vm.OverlayForWifi);
        Assert.Equal(4, vm.OverlaySeconds);
    }

    [Fact]
    public void Save_writes_every_switch_through_to_the_file()
    {
        var services = Build(new AppSettings());
        var vm = Card(services);
        vm.HotkeysEnabled = true;
        vm.OverlayForFanMode = true;
        vm.OverlayForBacklight = true;
        vm.OverlayForTouchpad = true;
        vm.OverlayForWifi = true;
        vm.OverlaySeconds = 3;

        vm.SaveCommand.Execute(null);

        var reloaded = new SettingsStore(services.Store.Path).Load().Hotkeys;
        Assert.True(reloaded.Enabled);
        Assert.True(reloaded.OverlayForFanMode);
        Assert.True(reloaded.OverlayForBacklight);
        Assert.True(reloaded.OverlayForTouchpad);
        Assert.True(reloaded.OverlayForWifi);
        Assert.Equal(3, reloaded.OverlaySeconds);
    }

    /// <summary>
    /// The service holds the same settings object, so a switch takes effect on the next keypress.
    /// </summary>
    /// <remarks>The point of writing back into <c>Settings.Hotkeys</c> rather than only to disk:
    /// <see cref="HotkeyService"/> was handed the live instance, and an owner who unticks an
    /// overlay expects the next press not to draw one, not the next launch.</remarks>
    [Fact]
    public void Save_reaches_the_running_hotkey_service_and_not_only_the_disk()
    {
        var services = Build(new AppSettings(), listening: true);
        var vm = Card(services);
        vm.OverlayForTouchpad = true;

        vm.SaveCommand.Execute(null);

        Assert.True(services.Settings.Hotkeys.OverlayForTouchpad);
    }

    [Theory]
    [InlineData(0, HotkeySettings.MinOverlaySeconds)]
    [InlineData(-5, HotkeySettings.MinOverlaySeconds)]
    [InlineData(900, HotkeySettings.MaxOverlaySeconds)]
    public void A_duration_outside_the_range_is_corrected_in_the_box_the_owner_typed_it_into(
        int typed, int expected)
    {
        // Silently saving a different number than the one still showing would leave the card
        // claiming a duration the app does not use - the same rule the poll intervals follow.
        var services = Build(new AppSettings());
        var vm = Card(services);
        vm.OverlaySeconds = typed;

        vm.SaveCommand.Execute(null);

        Assert.Equal(expected, vm.OverlaySeconds);
        Assert.Equal(expected, new SettingsStore(services.Store.Path).Load().Hotkeys.OverlaySeconds);
        // And said out loud: a number that changes under the owner's hands with no explanation
        // reads as the app having lost what was typed.
        Assert.Contains("brought inside", LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duration_the_app_accepts_is_saved_without_comment()
    {
        var vm = Card(Build(new AppSettings(), listening: true));
        vm.OverlaySeconds = 2;

        vm.SaveCommand.Execute(null);

        Assert.Equal("Hotkey settings saved.", LastMessage);
    }

    /// <summary>The card has no status line of its own; it writes to the dialog's.</summary>
    /// <remarks>Two status lines in one dialog would leave the owner reading whichever one was
    /// stale. This is the wiring that keeps there being one.</remarks>
    [Fact]
    public void What_the_card_says_lands_on_the_dialogs_own_status_line()
    {
        var settings = new SettingsViewModel(Build(new AppSettings(), listening: true));

        settings.Hotkeys.SaveCommand.Execute(null);

        Assert.Contains("Hotkey settings saved", settings.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_listening_on_when_nothing_is_listening_says_a_restart_is_needed()
    {
        // The channels are opened once, in AppServices.Create, and only when the saved settings
        // already asked for them. Saving Enabled=true mid-run changes the file and nothing else,
        // and an owner left pressing Fn keys at a program that is not listening deserves to know.
        var vm = Card(Build(new AppSettings()));
        vm.HotkeysEnabled = true;

        vm.SaveCommand.Execute(null);

        Assert.Contains("restart", LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nothing_asks_for_a_restart_when_the_channels_are_already_open()
    {
        var vm = Card(Build(new AppSettings(), listening: true));
        vm.HotkeysEnabled = true;

        vm.SaveCommand.Execute(null);

        Assert.NotEqual("", LastMessage);
        Assert.DoesNotContain("restart", LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Switching_listening_off_asks_for_nothing()
    {
        // Off takes effect immediately whether or not the channels are open: HotkeyPolicy.Decide
        // reads the live Enabled flag and stops every signal dead.
        var vm = Card(Build(new AppSettings(), listening: true));
        vm.HotkeysEnabled = false;

        vm.SaveCommand.Execute(null);

        Assert.DoesNotContain("restart", LastMessage, StringComparison.OrdinalIgnoreCase);
    }
}
