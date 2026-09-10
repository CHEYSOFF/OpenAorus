using OpenAorus.Hardware.Config;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The persisted proof, and every way a file on disk could claim more than it earned.
/// </summary>
public class SchemaRecordTests
{
    [Fact]
    public void A_fresh_record_claims_nothing()
    {
        var r = new SchemaRecord();

        Assert.False(r.Registered);
        Assert.False(r.GatesPassed);
        Assert.Null(r.Fingerprint);
        Assert.False(r.Repair());
    }

    [Fact]
    public void A_passed_record_with_no_fingerprint_cannot_be_trusted_and_is_cleared()
    {
        // Without the fingerprint there is nothing to compare the live schema against, so the
        // pass could not be invalidated even if the schema changed underneath it.
        var r = new SchemaRecord { Registered = true, GatesPassed = true, Fingerprint = null };

        Assert.True(r.Repair());
        Assert.False(r.GatesPassed);
    }

    [Fact]
    public void A_pass_earned_over_a_registration_this_app_did_not_make_is_kept()
    {
        // The commonest machine there is: Control Center installed and working, and the owner has
        // run the two checks against it. The gates prove a mapping, not an ownership, so there is
        // nothing wrong with this record - and clearing it, which is what used to happen on every
        // load, switched fan and battery control off on exactly those machines with no way back,
        // since registering over Control Center's schema is refused and always will be.
        var r = new SchemaRecord
        {
            Registered = false, GatesPassed = true, Fingerprint = "aaa",
            When = DateTime.Now.AddMinutes(-1),
        };

        Assert.False(r.Repair());
        Assert.True(r.GatesPassed);
    }

    [Fact]
    public void A_record_dated_in_the_future_is_cleared()
    {
        var r = new SchemaRecord
        {
            Registered = true, GatesPassed = true, Fingerprint = "aaa",
            When = DateTime.Now.AddDays(2),
        };

        Assert.True(r.Repair());
        Assert.False(r.GatesPassed);
    }

    [Fact]
    public void A_record_with_no_date_at_all_is_cleared()
    {
        // Every pass this app records is stamped when it happened. One without a stamp was not
        // written by a gate run.
        var r = new SchemaRecord { Registered = true, GatesPassed = true, Fingerprint = "aaa", When = null };

        Assert.True(r.Repair());
        Assert.False(r.GatesPassed);
    }

    [Fact]
    public void A_sound_record_is_left_alone()
    {
        var r = new SchemaRecord
        {
            Registered = true, GatesPassed = true, Fingerprint = "aaa", When = DateTime.Now.AddHours(-1),
        };

        Assert.False(r.Repair());
        Assert.True(r.GatesPassed);
    }

    [Fact]
    public void An_hour_of_clock_slack_is_not_treated_as_a_forged_date()
    {
        // A file written just before the clocks go back reads as up to an hour in the future on a
        // machine nothing has happened to.
        var r = new SchemaRecord
        {
            Registered = true, GatesPassed = true, Fingerprint = "aaa",
            When = DateTime.Now.AddMinutes(30),
        };

        Assert.False(r.Repair());
        Assert.True(r.GatesPassed);
    }

    [Fact]
    public void Repairing_never_grants_a_pass_it_only_takes_one_away()
    {
        foreach (var r in new[]
                 {
                     new SchemaRecord(),
                     new SchemaRecord { Registered = true },
                     new SchemaRecord { Registered = true, Fingerprint = "aaa" },
                 })
        {
            r.Repair();
            Assert.False(r.GatesPassed);
        }
    }

    [Fact]
    public void A_pass_is_only_a_pass_for_the_schema_it_was_earned_against()
    {
        // Control Center installed afterwards, or a hand-run mofcomp: same machine, different
        // mapping, and every id the gates proved is now an id nobody has checked.
        var r = Passed("aaa");

        Assert.True(r.ProvenFor("aaa"));
        Assert.False(r.ProvenFor("bbb"));
    }

    [Fact]
    public void A_schema_that_could_not_be_fingerprinted_matches_nothing()
    {
        Assert.False(Passed("aaa").ProvenFor(null));
    }

    [Fact]
    public void A_record_that_did_not_pass_proves_nothing_however_well_the_fingerprint_matches()
    {
        var r = Passed("aaa");
        r.GatesPassed = false;

        Assert.False(r.ProvenFor("aaa"));
    }

    [Fact]
    public void Who_registered_the_schema_has_no_say_in_what_the_pass_proves()
    {
        // The fingerprint is the whole of the safety: it says these classes bind today exactly
        // what the gates were run against. Who compiled them changes nothing about that, and the
        // drift test is unaffected either way.
        var r = Passed("aaa");
        r.Registered = false;

        Assert.True(r.ProvenFor("aaa"));
        Assert.False(r.ProvenFor("bbb"));
        Assert.False(r.ProvenFor(null));
    }

    [Fact]
    public void Settings_always_have_a_schema_record_even_from_an_older_file()
    {
        // Same rule as Lighting and Hotkeys: a file written by v0.1 has no schema section at all.
        var s = new AppSettings { Schema = null! };

        Assert.NotNull(s.Schema);
        Assert.False(s.Schema.GatesPassed);
    }

    private static SchemaRecord Passed(string fingerprint) => new()
    {
        Registered = true,
        GatesPassed = true,
        Fingerprint = fingerprint,
        When = DateTime.Now.AddMinutes(-1),
    };
}
