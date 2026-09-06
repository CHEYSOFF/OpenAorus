using System.IO;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class LightingSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "settings.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private void WriteSettingsFile(string json)
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, json);
    }

    [Fact]
    public void A_fresh_AppSettings_has_usable_lighting_defaults()
    {
        var lighting = new AppSettings().Lighting;
        Assert.NotNull(lighting);
        Assert.Equal(LightEffect.Static, lighting.Effect);
        Assert.Equal(50, lighting.BrightnessPercent);
        Assert.Empty(lighting.PerKeyColors);
        Assert.Null(lighting.LayoutOverride);
    }

    [Fact]
    public void ToParameters_and_From_round_trip()
    {
        var p = EffectParameters.Default(LightEffect.Wave) with
        {
            Color = new RgbColor(1, 2, 3),
            SecondColor = new RgbColor(4, 5, 6),
            SpeedPercent = 70,
            BrightnessPercent = 30,
            Direction = LightDirection.Down,
            Random = true,
        };
        var settings = new LightingSettings();
        settings.From(p);
        Assert.Equal(p, settings.ToParameters());
    }

    [Fact]
    public void Lighting_survives_a_save_and_load_including_per_key_colours()
    {
        var store = new SettingsStore(File);
        var settings = store.Load();
        settings.Lighting.Effect = LightEffect.Custom;
        settings.Lighting.BrightnessPercent = 80;
        settings.Lighting.LayoutOverride = KeyboardLayout.EngUs;
        settings.Lighting.PerKeyColors = Enumerable.Range(0, KeyLayout.SlotCount)
            .Select(i => new RgbColor((byte)i, 0, 0)).ToList();
        store.Save(settings);

        var reloaded = new SettingsStore(File);
        var back = reloaded.Load();
        Assert.Equal(LightEffect.Custom, back.Lighting.Effect);
        Assert.Equal(80, back.Lighting.BrightnessPercent);
        Assert.Equal(KeyboardLayout.EngUs, back.Lighting.LayoutOverride);
        Assert.Equal(settings.Lighting.PerKeyColors, back.Lighting.PerKeyColors);
        Assert.False(reloaded.LastLoadWasReset);
        Assert.False(reloaded.LastLoadRepaired);
    }

    [Fact]
    public void Enums_persist_as_names_not_numbers()
    {
        var store = new SettingsStore(File);
        var settings = store.Load();
        settings.Lighting.Effect = LightEffect.Breathing;
        settings.Lighting.Direction = LightDirection.Left;
        store.Save(settings);
        var json = System.IO.File.ReadAllText(File);
        Assert.Contains("\"Breathing\"", json);
        Assert.Contains("\"Left\"", json);
    }

    [Fact]
    public void Presets_survive_a_save_and_load()
    {
        var store = new SettingsStore(File);
        var settings = store.Load();
        settings.Lighting.Presets.Add(new LightingPreset
        {
            Name = "Desk lamp",
            Effect = LightEffect.Wave,
            Color = new RgbColor(9, 8, 7),
            SpeedPercent = 20,
            BrightnessPercent = 35,
            Direction = LightDirection.Up,
            Random = true,
        });
        store.Save(settings);

        var back = new SettingsStore(File).Load();
        var preset = Assert.Single(back.Lighting.Presets);
        Assert.Equal("Desk lamp", preset.Name);
        Assert.Equal(LightEffect.Wave, preset.Effect);
        Assert.Equal(new RgbColor(9, 8, 7), preset.Color);
        Assert.Equal(20, preset.SpeedPercent);
        Assert.Equal(35, preset.BrightnessPercent);
        Assert.Equal(LightDirection.Up, preset.Direction);
        Assert.True(preset.Random);
        Assert.Null(preset.PerKeyColors);
    }

    [Fact]
    public void Built_in_presets_are_named_and_valid()
    {
        var presets = LightingSettings.BuiltInPresets;
        Assert.Equal(3, presets.Count);
        Assert.All(presets, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        Assert.Contains(presets, p => p.Name == "Off" && p.BrightnessPercent == 0);
        Assert.Equal(presets.Select(p => p.Name).Distinct().Count(), presets.Count);
    }

    [Fact]
    public void Built_in_presets_all_build_a_packet()
    {
        foreach (var preset in LightingSettings.BuiltInPresets)
        {
            var settings = new LightingSettings();
            settings.From(preset.ToParameters());
            Assert.False(settings.Repair(), $"'{preset.Name}' needed repair.");
            var packet = EffectPacket.Build(settings.ToParameters());
            Assert.Equal(KeyboardHid.ReportLength, packet.Length);
        }
    }

    [Fact]
    public void Settings_written_before_lighting_existed_still_load()
    {
        WriteSettingsFile("{ \"Mode\": \"Quiet\", \"FixedPercent\": 40 }");
        var store = new SettingsStore(File);
        var loaded = store.Load();
        Assert.NotNull(loaded.Lighting);
        Assert.Equal(LightEffect.Static, loaded.Lighting.Effect);
        Assert.Equal(FanMode.Quiet, loaded.Mode);
        Assert.Equal(40, loaded.FixedPercent);
        Assert.False(store.LastLoadWasReset);
        Assert.False(store.LastLoadRepaired);
    }

    // ---- Malformed persisted state -------------------------------------------------
    // Everything below feeds SettingsStore a file no honest run of the app could have
    // written. The contract is that the owner gets working defaults and is told, never
    // an exception thrown out of startup or an effect id that detonates in EffectPacket.

    [Fact]
    public void A_missing_file_gives_lighting_defaults_and_records_nothing()
    {
        var store = new SettingsStore(File);
        var loaded = store.Load();
        Assert.Equal(LightEffect.Static, loaded.Lighting.Effect);
        Assert.Empty(loaded.Lighting.Presets);
        Assert.False(store.LastLoadWasReset);
        Assert.False(store.LastLoadRepaired);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ \"Mode\": \"Quiet\", \"Lighting\": { \"Effect\": \"Wave\", ")]  // truncated mid-object
    [InlineData("[1, 2, 3]")]                                                       // valid JSON, wrong shape
    [InlineData("null")]                                                            // valid JSON, no object at all
    [InlineData("42")]
    [InlineData("{ \"Lighting\": 5 }")]                                             // lighting is not an object
    [InlineData("{ \"Lighting\": { \"Effect\": \"NoSuchEffect\" } }")]              // unknown enum name
    [InlineData("{ \"Lighting\": { \"Color\": { \"R\": 300 } } }")]                 // colour byte out of range
    public void An_unreadable_file_gives_defaults_and_a_recorded_reset(string json)
    {
        WriteSettingsFile(json);
        var store = new SettingsStore(File);

        AppSettings? loaded = null;
        Assert.Null(Record.Exception(() => loaded = store.Load()));

        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.Lighting);
        Assert.Equal(LightEffect.Static, loaded.Lighting.Effect);
        Assert.Equal(50, loaded.Lighting.BrightnessPercent);
        Assert.Equal(FanMode.Normal, loaded.Mode);
        Assert.True(store.LastLoadWasReset);
        Assert.True(System.IO.File.Exists(File + ".bad"));
    }

    [Fact]
    public void An_out_of_range_effect_id_is_repaired_without_discarding_the_fan_settings()
    {
        WriteSettingsFile("{ \"Mode\": \"Quiet\", \"FixedPercent\": 40, \"Lighting\": { \"Effect\": 99 } }");
        var store = new SettingsStore(File);
        var loaded = store.Load();

        Assert.Equal(LightEffect.Static, loaded.Lighting.Effect);
        Assert.True(store.LastLoadRepaired);
        // A single bad value is not a corrupt file: the rest of the owner's settings stay.
        Assert.False(store.LastLoadWasReset);
        Assert.Equal(FanMode.Quiet, loaded.Mode);
        Assert.Equal(40, loaded.FixedPercent);
        Assert.False(System.IO.File.Exists(File + ".bad"));
    }

    [Fact]
    public void Out_of_range_percentages_are_clamped()
    {
        WriteSettingsFile("{ \"Lighting\": { \"SpeedPercent\": -40, \"BrightnessPercent\": 500 } }");
        var store = new SettingsStore(File);
        var loaded = store.Load();

        Assert.Equal(0, loaded.Lighting.SpeedPercent);
        Assert.Equal(100, loaded.Lighting.BrightnessPercent);
        Assert.True(store.LastLoadRepaired);
    }

    [Fact]
    public void An_out_of_range_direction_or_layout_falls_back()
    {
        WriteSettingsFile("{ \"Lighting\": { \"Direction\": 77, \"LayoutOverride\": 77 } }");
        var store = new SettingsStore(File);
        var loaded = store.Load();

        Assert.Equal(LightDirection.Right, loaded.Lighting.Direction);
        Assert.Null(loaded.Lighting.LayoutOverride);
        Assert.True(store.LastLoadRepaired);
    }

    [Fact]
    public void A_null_lighting_section_never_reaches_the_caller()
    {
        WriteSettingsFile("{ \"Mode\": \"Gaming\", \"Lighting\": null }");
        var store = new SettingsStore(File);
        var loaded = store.Load();

        Assert.NotNull(loaded.Lighting);
        Assert.Equal(LightEffect.Static, loaded.Lighting.Effect);
        Assert.Empty(loaded.Lighting.PerKeyColors);
        Assert.Empty(loaded.Lighting.Presets);
        Assert.Equal(FanMode.Gaming, loaded.Mode);
    }

    [Fact]
    public void A_null_colour_list_becomes_an_empty_one()
    {
        WriteSettingsFile("{ \"Lighting\": { \"PerKeyColors\": null, \"Presets\": null } }");
        var store = new SettingsStore(File);
        var loaded = store.Load();

        Assert.Empty(loaded.Lighting.PerKeyColors);
        Assert.Empty(loaded.Lighting.Presets);
    }

    [Fact]
    public void A_wrong_length_colour_list_is_dropped_rather_than_half_applied()
    {
        WriteSettingsFile(
            "{ \"Lighting\": { \"Effect\": \"Custom\", \"PerKeyColors\": [ { \"R\": 1, \"G\": 2, \"B\": 3 } ] } }");
        var store = new SettingsStore(File);
        var loaded = store.Load();

        Assert.Empty(loaded.Lighting.PerKeyColors);
        Assert.True(store.LastLoadRepaired);
    }

    [Fact]
    public void Unusable_presets_are_dropped_and_the_rest_repaired()
    {
        WriteSettingsFile(
            "{ \"Lighting\": { \"Presets\": [ null, { \"Name\": \"  \" }, " +
            "{ \"Name\": \"Bad\", \"Effect\": 99, \"BrightnessPercent\": 900, \"PerKeyColors\": [] } ] } }");
        var store = new SettingsStore(File);
        var loaded = store.Load();

        var preset = Assert.Single(loaded.Lighting.Presets);
        Assert.Equal("Bad", preset.Name);
        Assert.Equal(LightEffect.Static, preset.Effect);
        Assert.Equal(100, preset.BrightnessPercent);
        Assert.Null(preset.PerKeyColors);
        Assert.True(store.LastLoadRepaired);
    }

    [Fact]
    public async Task A_repaired_file_applies_to_the_keyboard_instead_of_throwing()
    {
        WriteSettingsFile(
            "{ \"Lighting\": { \"Effect\": 99, \"SpeedPercent\": 4000, \"BrightnessPercent\": -1, \"Direction\": 200 } }");
        var loaded = new SettingsStore(File).Load();

        var hid = new FakeKeyboardHid();
        var controller = new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUs), _ => Task.CompletedTask);
        var result = await controller.ApplyEffectAsync(loaded.Lighting.ToParameters());

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void Repair_reports_that_it_changed_nothing_for_a_settled_file()
    {
        var settings = new LightingSettings
        {
            Effect = LightEffect.Wave,
            SpeedPercent = 0,
            BrightnessPercent = 100,
            Direction = LightDirection.CounterClockwise,
            LayoutOverride = KeyboardLayout.EngUk,
            PerKeyColors = Enumerable.Repeat(RgbColor.White, KeyLayout.SlotCount).ToList(),
        };
        Assert.False(settings.Repair());
        Assert.Equal(LightEffect.Wave, settings.Effect);
        Assert.Equal(KeyLayout.SlotCount, settings.PerKeyColors.Count);
    }
}
