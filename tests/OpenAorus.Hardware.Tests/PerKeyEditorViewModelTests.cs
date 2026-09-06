using System.IO;
using OpenAorus.App;
using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Lighting;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;
using OpenAorus.Hardware.Ui;
using MediaColor = System.Windows.Media.Color;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Drives the per-key editor over a real <see cref="LightingController"/> and a fake HID, so what
/// is asserted is the bytes that would reach the keyboard rather than the editor's own opinion of
/// them. The colours are read back out of the recorded reports with <see cref="PerKeyPacket.Parse"/>,
/// which is the same code path the keyboard's own answers go through.
/// </summary>
public class PerKeyEditorViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static Task NoDelay(int _) => Task.CompletedTask;

    private sealed class Harness
    {
        public required FakeKeyboardHid Hid { get; init; }
        public required AppServices Services { get; init; }
        public required KeyLayout Layout { get; init; }
        public List<(BannerKind Kind, string Text)> Banners { get; } = new();
        public int Brightness { get; set; } = 50;

        public PerKeyEditorViewModel Editor() =>
            new(Services, (kind, text) => Banners.Add((kind, text)), () => Brightness);

        /// <summary>The 128 colours carried by the two 0x06 reports the editor just sent.</summary>
        public RgbColor[] Sent()
        {
            var colorReports = Hid.Written.Where(r => r[1] == 0x06).ToList();
            Assert.Equal(2, colorReports.Count);
            return PerKeyPacket.Parse(colorReports[0], colorReports[1]);
        }

        public AppSettings Reload() => new SettingsStore(Services.Store.Path).Load();
    }

    private Harness Build(KeyboardLayout which = KeyboardLayout.EngUk, bool keyboardPresent = true)
    {
        var hid = new FakeKeyboardHid { IsPresent = keyboardPresent };
        var wmi = new FakeGigabyteWmi();
        var profile = ModelProfile.Detect("AORUS 17G KD");
        var layout = KeyLayout.For(which);
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N"), "settings.json");

        return new Harness
        {
            Hid = hid,
            Layout = layout,
            Services = new AppServices
            {
                Profile = profile,
                Wmi = wmi,
                Fans = new FanController(wmi, profile, delay: _ => Task.CompletedTask),
                Sensors = new SensorReader(wmi, profile),
                Battery = new BatteryController(wmi),
                Store = new SettingsStore(path),
                Settings = new AppSettings(),
                Gcc = new FakeGccSystem(),
                Lighting = new LightingController(hid, layout, NoDelay),
                KeyboardPresent = keyboardPresent,
                Version = "0.0.0",
                ExePath = "OpenAorus.Tests.exe",
            },
        };
    }

    private static MediaColor Media(byte r, byte g, byte b) => MediaColor.FromRgb(r, g, b);
    private static readonly MediaColor Black = Media(0, 0, 0);

    // ---- What the editor is made of -------------------------------------------------

    [Theory]
    [InlineData(KeyboardLayout.EngUs)]
    [InlineData(KeyboardLayout.EngUk)]
    public void The_editor_offers_every_populated_slot_and_nothing_else(KeyboardLayout which)
    {
        var h = Build(which);

        var editor = h.Editor();

        Assert.Equal(
            h.Layout.RealKeys.Select(k => k.Slot).OrderBy(s => s),
            editor.Keys.Select(k => k.Slot).OrderBy(s => s));
        Assert.DoesNotContain(editor.Keys, k => k.Name == KeyLayout.Unused);
        foreach (var key in editor.Keys)
            Assert.Equal(h.Layout.NameAt(key.Slot), key.Name);
    }

    /// <summary>
    /// The picture and the flat list are two views of the same keys, not two copies: painting
    /// through one has to change the other, because the picture is what is clicked and the flat
    /// list is what is sent.
    /// </summary>
    [Fact]
    public void The_drawn_keys_are_the_same_objects_as_the_listed_keys()
    {
        var editor = Build().Editor();

        var drawn = editor.Blocks.SelectMany(b => b.Rows).SelectMany(r => r.Keys).ToList();

        Assert.Equal(editor.Keys.Count, drawn.Count);
        foreach (var key in drawn) Assert.Contains(key, editor.Keys);
    }

    [Fact]
    public void The_editor_starts_from_the_saved_colours()
    {
        var h = Build();
        var saved = Enumerable.Range(0, KeyLayout.SlotCount).Select(i => new RgbColor((byte)i, 0x10, 0x20)).ToList();
        h.Services.Settings.Lighting.PerKeyColors = saved;

        var editor = h.Editor();

        foreach (var key in editor.Keys)
            Assert.Equal(Media((byte)key.Slot, 0x10, 0x20), key.Color);
    }

    // ---- Painting -------------------------------------------------------------------

    /// <summary>
    /// The test the whole feature rests on. The slots are a scan matrix, so a mapping mistake
    /// looks entirely plausible on screen and only shows up as the wrong key lighting. Painting
    /// each key on its own and reading the bytes back proves the editor sends exactly the one
    /// slot the key's name resolves to, for every key on both layouts.
    /// </summary>
    [Theory]
    [InlineData(KeyboardLayout.EngUs)]
    [InlineData(KeyboardLayout.EngUk)]
    public async Task Painting_one_key_colours_that_key_s_slot_and_no_other(KeyboardLayout which)
    {
        var h = Build(which);
        var editor = h.Editor();
        editor.BrushColor = Media(0x11, 0x22, 0x33);

        foreach (var key in editor.Keys)
        {
            editor.ClearCommand.Execute(null);
            editor.PaintCommand.Execute(key);
            h.Hid.ClearWritten();

            await editor.ApplyCommand.ExecuteAsync(null);

            var sent = h.Sent();
            Assert.Equal(new RgbColor(0x11, 0x22, 0x33), sent[key.Slot]);
            Assert.Equal(h.Layout.NameAt(key.Slot), key.Name);
            for (var slot = 0; slot < KeyLayout.SlotCount; slot++)
                if (slot != key.Slot)
                    Assert.Equal(RgbColor.Black, sent[slot]);
        }
    }

    [Fact]
    public void Painting_nothing_is_not_an_error()
    {
        var editor = Build().Editor();

        editor.PaintCommand.Execute(null);

        Assert.All(editor.Keys, k => Assert.Equal(Black, k.Color));
    }

    /// <summary>
    /// The unused slots are not keys, so they have no place in the editor at all - and the array
    /// that goes to the hardware still has to be a full 128, with black where the keyboard has
    /// nothing to light.
    /// </summary>
    [Fact]
    public async Task Unpopulated_slots_cannot_be_painted_and_are_sent_black()
    {
        var h = Build();
        var editor = h.Editor();
        editor.BrushColor = Media(0xFF, 0xFF, 0xFF);

        editor.FillAllCommand.Execute(null);
        await editor.ApplyCommand.ExecuteAsync(null);

        var sent = h.Sent();
        Assert.Equal(KeyLayout.SlotCount, sent.Length);
        for (var slot = 0; slot < KeyLayout.SlotCount; slot++)
        {
            var expected = h.Layout.NameAt(slot) == KeyLayout.Unused
                ? RgbColor.Black
                : new RgbColor(0xFF, 0xFF, 0xFF);
            Assert.Equal(expected, sent[slot]);
        }
    }

    [Fact]
    public async Task Clearing_resets_every_slot_to_black()
    {
        var h = Build();
        var editor = h.Editor();
        editor.BrushColor = Media(0x40, 0x50, 0x60);
        editor.FillAllCommand.Execute(null);

        editor.ClearCommand.Execute(null);
        await editor.ApplyCommand.ExecuteAsync(null);

        Assert.All(editor.Keys, k => Assert.Equal(Black, k.Color));
        Assert.All(h.Sent(), c => Assert.Equal(RgbColor.Black, c));
    }

    /// <summary>
    /// A dab paints one key; a drag paints the ones it crosses. The drag latch lives here rather
    /// than in the canvas's code-behind so that it is reachable by a test at all.
    /// </summary>
    [Fact]
    public void A_drag_paints_the_keys_it_crosses_and_a_hover_alone_paints_nothing()
    {
        var editor = Build().Editor();
        editor.BrushColor = Media(1, 2, 3);
        var first = editor.Keys[0];
        var second = editor.Keys[1];
        var untouched = editor.Keys[2];

        editor.PaintOver(second);                 // hovering with the button up
        Assert.Equal(Black, second.Color);

        editor.BeginPaint();
        editor.PaintCommand.Execute(first);
        editor.PaintOver(second);
        editor.EndPaint();
        editor.PaintOver(untouched);              // the button is up again

        Assert.Equal(Media(1, 2, 3), first.Color);
        Assert.Equal(Media(1, 2, 3), second.Color);
        Assert.Equal(Black, untouched.Color);
        Assert.False(editor.IsPainting);
    }

    /// <summary>
    /// Painting one key at a time is how the owner settles whether the recovered slot order is the
    /// right one for their keyboard, so the editor has to say which slot it just painted.
    /// </summary>
    [Fact]
    public void The_editor_names_the_slot_it_last_painted()
    {
        var h = Build();
        var editor = h.Editor();
        var key = editor.Keys.Single(k => k.Name == "A");

        editor.PaintCommand.Execute(key);

        Assert.True(key.IsSelected);
        Assert.Contains("A", editor.StatusText);
        Assert.Contains(key.Slot.ToString(), editor.StatusText);
        Assert.Equal(h.Layout.IndexOf("A"), key.Slot);
    }

    [Fact]
    public void Only_the_last_painted_key_is_marked()
    {
        var editor = Build().Editor();

        editor.PaintCommand.Execute(editor.Keys[0]);
        editor.PaintCommand.Execute(editor.Keys[1]);

        Assert.False(editor.Keys[0].IsSelected);
        Assert.True(editor.Keys[1].IsSelected);
    }

    // ---- Applying -------------------------------------------------------------------

    [Fact]
    public async Task Applying_writes_the_colours_then_selects_custom_and_saves()
    {
        var h = Build();
        h.Brightness = 71;
        var editor = h.Editor();
        editor.BrushColor = Media(0x0A, 0x0B, 0x0C);
        editor.FillAllCommand.Execute(null);

        await editor.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(4, h.Hid.Written.Count);
        Assert.Equal(0x06, h.Hid.Command(0));
        Assert.Equal(0x06, h.Hid.Command(1));
        Assert.Equal(0x82, h.Hid.Command(2));   // the read-modify-write of the shared block
        Assert.Equal(0x02, h.Hid.Command(3));
        Assert.Equal((byte)LightEffect.Custom, h.Hid.Written[3][10]);
        Assert.Equal(71, h.Hid.Written[3][12]);

        var reloaded = h.Reload().Lighting;
        Assert.Equal(LightEffect.Custom, reloaded.Effect);
        Assert.Equal(KeyLayout.SlotCount, reloaded.PerKeyColors.Count);
        Assert.Equal(new RgbColor(0x0A, 0x0B, 0x0C), reloaded.PerKeyColors[h.Layout.IndexOf("A")]);
    }

    /// <summary>
    /// Every key a different colour, sent and read back: this is what catches a plane-major
    /// packing mistake, which an all-one-colour painting cannot see.
    /// </summary>
    [Fact]
    public async Task A_full_painting_round_trips_through_the_report_format()
    {
        var h = Build();
        var editor = h.Editor();
        foreach (var key in editor.Keys)
            key.Color = Media((byte)(key.Slot * 2), (byte)(key.Slot + 3), (byte)(255 - key.Slot));

        await editor.ApplyCommand.ExecuteAsync(null);

        var sent = h.Sent();
        foreach (var key in editor.Keys)
            Assert.Equal(new RgbColor((byte)(key.Slot * 2), (byte)(key.Slot + 3), (byte)(255 - key.Slot)), sent[key.Slot]);
    }

    [Fact]
    public async Task A_failed_write_reaches_the_owner_and_saves_nothing()
    {
        var h = Build();
        h.Hid.FailNextWrite = true; // the first 0x06 colour report
        var editor = h.Editor();

        await editor.ApplyCommand.ExecuteAsync(null);

        var (kind, text) = Assert.Single(h.Banners);
        Assert.Equal(BannerKind.Error, kind);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.False(File.Exists(h.Services.Store.Path));
    }

    [Fact]
    public async Task An_absent_keyboard_writes_nothing()
    {
        var h = Build(keyboardPresent: false);
        var editor = h.Editor();

        await editor.ApplyCommand.ExecuteAsync(null);
        await editor.ReadFromKeyboardCommand.ExecuteAsync(null);

        Assert.Empty(h.Hid.Written);
        Assert.Empty(h.Banners);
    }

    // ---- Reading back ---------------------------------------------------------------

    [Fact]
    public async Task Reading_from_the_keyboard_loads_what_it_answers_with()
    {
        var h = Build();
        var onDevice = Enumerable.Range(0, KeyLayout.SlotCount)
            .Select(i => new RgbColor((byte)(i + 1), (byte)(i + 2), (byte)(i + 3))).ToList();
        var (first, second) = PerKeyPacket.BuildWrite(onDevice);
        h.Hid.Responses.Enqueue(first);
        h.Hid.Responses.Enqueue(second);
        var editor = h.Editor();

        await editor.ReadFromKeyboardCommand.ExecuteAsync(null);

        foreach (var key in editor.Keys)
            Assert.Equal(Media((byte)(key.Slot + 1), (byte)(key.Slot + 2), (byte)(key.Slot + 3)), key.Color);
    }

    [Fact]
    public async Task A_keyboard_that_does_not_answer_leaves_the_painting_alone()
    {
        var h = Build();
        var editor = h.Editor();
        editor.BrushColor = Media(9, 9, 9);
        editor.FillAllCommand.Execute(null);

        await editor.ReadFromKeyboardCommand.ExecuteAsync(null); // no queued responses

        Assert.All(editor.Keys, k => Assert.Equal(Media(9, 9, 9), k.Color));
        Assert.False(string.IsNullOrWhiteSpace(editor.StatusText));
    }

    // ---- The brush ------------------------------------------------------------------

    [Fact]
    public void The_hex_box_and_the_brush_follow_each_other()
    {
        var editor = Build().Editor();

        editor.BrushColor = Media(0x12, 0x34, 0x56);
        Assert.Equal("#123456", editor.BrushHex);

        editor.BrushHex = "#ABCDEF";
        Assert.Equal(Media(0xAB, 0xCD, 0xEF), editor.BrushColor);
    }

    [Fact]
    public void A_half_typed_hex_value_does_not_change_the_brush()
    {
        var editor = Build().Editor();
        editor.BrushColor = Media(0x12, 0x34, 0x56);

        editor.BrushHex = "#AB";

        Assert.Equal(Media(0x12, 0x34, 0x56), editor.BrushColor);
    }
}
