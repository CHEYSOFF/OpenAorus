using System.IO;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The checked-in MOF and the research file it claims to come from, held together.
/// </summary>
/// <remarks>
/// <para>
/// This is the mechanism behind the design's first rule: reproduce, do not invent. The MOF is
/// checked in so a human can read the 143 numbers before they reach the controller, and it is
/// regenerated here on every test run so that reading stays honest. Edit the .mof by hand and this
/// goes red; edit the research file and this goes red until the .mof is regenerated.
/// </para>
/// <para>
/// TO REGENERATE: set <c>OPENAORUS_WRITE_MOF=1</c> and run this test. It writes the generated text
/// over the checked-in files instead of asserting, and the diff is then reviewable in the commit.
/// That is the whole procedure - there is deliberately no separate script, because a script that
/// lives somewhere else is a script that stops matching the check.
/// </para>
/// <para>
/// REGENERATING IS NOT ENOUGH TO MAKE A CHANGED MAPPING GREEN, and that is the point. This file
/// only says the MOF and the research file agree; it says nothing about whether either is still
/// the schema this app was built against. <see cref="SchemaFingerprintTests.RecoveredBinding"/> is
/// what says that, and no regeneration step can rewrite a literal in test source.
/// </para>
/// </remarks>
public class MofDriftTests
{
    private static WmiSchema Recovered() => WmiSchemaParser.Parse(File.ReadAllText(WmiSchemaParserTests.DumpPath));

    [Fact]
    public void The_checked_in_mof_is_what_the_generator_prints_from_the_research_file()
    {
        var schema = Recovered();
        var expectedInstall = MofWriter.WriteInstall(schema, SchemaFingerprint.Of(schema));
        var expectedRemove = MofWriter.WriteRemove(schema);

        if (Environment.GetEnvironmentVariable("OPENAORUS_WRITE_MOF") == "1")
        {
            File.WriteAllText(RepoPath("GB_WMIACPI.mof"), expectedInstall);
            File.WriteAllText(RepoPath("GB_WMIACPI-remove.mof"), expectedRemove);
            return;
        }

        Assert.Equal(expectedInstall, SchemaMof.Install);
        Assert.Equal(expectedRemove, SchemaMof.Remove);
    }

    [Fact]
    public void The_fingerprint_the_mof_records_is_the_fingerprint_of_the_schema_it_registers()
    {
        // The marker class carries this string into WMI and the startup check reads it back. If
        // it were computed from anything but the schema, that comparison would be circular.
        Assert.Equal(SchemaFingerprint.Of(Recovered()), SchemaMof.Fingerprint);
        Assert.Contains($"Fingerprint = \"{SchemaMof.Fingerprint}\";", SchemaMof.Install, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fingerprint_the_mof_records_is_the_one_written_down_in_the_test_suite()
    {
        // The tie between the file that reaches mofcomp and the literal nobody can regenerate.
        // Without it the marker instance could carry a number the suite never agreed to.
        Assert.Equal(SchemaFingerprintTests.RecoveredBinding, SchemaMof.Fingerprint);
    }

    [Fact]
    public void Both_files_ship_inside_the_assembly()
    {
        // mofcomp needs a path on disk, and a single-file publish has no loose files beside the
        // exe. They are embedded and written out at registration time - see Task 6.
        Assert.NotEmpty(SchemaMof.Install);
        Assert.NotEmpty(SchemaMof.Remove);
        Assert.Contains("GB_WMIACPI_Get", SchemaMof.Install, StringComparison.Ordinal);
    }

    [Fact]
    public void The_names_the_files_are_written_out_under_say_whose_they_are()
    {
        // They land in a temp directory beside whatever else is there. A file called
        // GB_WMIACPI.mof in %TEMP% is indistinguishable from Gigabyte's.
        Assert.StartsWith("OpenAorus-", SchemaMof.InstallFileName, StringComparison.Ordinal);
        Assert.StartsWith("OpenAorus-", SchemaMof.RemoveFileName, StringComparison.Ordinal);
        Assert.EndsWith(".mof", SchemaMof.InstallFileName, StringComparison.Ordinal);
        Assert.EndsWith(".mof", SchemaMof.RemoveFileName, StringComparison.Ordinal);
        Assert.NotEqual(SchemaMof.InstallFileName, SchemaMof.RemoveFileName);
    }

    [Fact]
    public void The_checked_in_install_file_carries_all_one_hundred_and_forty_three_numbers()
    {
        // Stated against the checked-in text rather than the generated one, so that a resource
        // that failed to embed - or embedded the wrong file - cannot read as a pass.
        var count = 0;
        for (var i = SchemaMof.Install.IndexOf("WmiMethodId(", StringComparison.Ordinal); i >= 0;
             i = SchemaMof.Install.IndexOf("WmiMethodId(", i + 1, StringComparison.Ordinal)) count++;

        Assert.Equal(143, count);
    }

    [Fact]
    public void The_two_embedded_files_are_not_the_same_file()
    {
        // A copy-paste in the csproj would give both LogicalNames the same source, and the
        // install text does contain everything the remove text needs to look plausible.
        Assert.NotEqual(SchemaMof.Install, SchemaMof.Remove);
        Assert.DoesNotContain("class GB_WMIACPI", SchemaMof.Remove, StringComparison.Ordinal);
    }

    private static string RepoPath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenAorus.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "OpenAorus.Hardware", "Wmi", "Schema", fileName);
    }
}
