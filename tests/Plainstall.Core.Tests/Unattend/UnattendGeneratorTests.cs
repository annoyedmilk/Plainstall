using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Plainstall.Core.Profiles;
using Plainstall.Core.Unattend;

namespace Plainstall.Core.Tests.Unattend;

public class UnattendGeneratorTests
{
    private const string TestPassword = "Correct horse 7! <&> ";

    // ---- virtual machine test mode ---------------------------------------------------------------

    private const string VmTestFile = "vm-test.xml";

    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    /// <summary>A Swiss PC with a German display language. The images are English or French on purpose.</summary>
    private static readonly HostFacts SwissHost = new("de-DE", "de-CH", "0807:00000807", "W. Europe Standard Time");

    private static readonly Case[] AllCases =
    [
        // (a) What the Recommended preset produces on x64: local account, name asked by Setup,
        //     privacy pages skipped, region, keyboard and time zone taken from the build PC.
        new("recommended.xml",
            InputFor(Options(new LocalAccountOptions { Name = null }, true)), null),

        // (b) Named account without a password, and a computer name.
        new("named-account.xml",
            InputFor(Options(new LocalAccountOptions { Name = "Anna" }, true, "ANNA-LAPTOP")), null),

        // (c) Named account with a password, and machine-scope first sign-in work.
        new("named-account-password.xml",
            InputFor(Options(new LocalAccountOptions { Name = "Anna", AskPassword = true }, true), machineScript: true),
            TestPassword),

        // (d) Everything on "ask" and no local account: the smallest file Plainstall writes.
        new("minimal-ask.xml",
            InputFor(Options(null, false, region: "ask", keyboard: "ask", timezone: "ask")), null),

        // (e) Explicit region, keyboard and time zone on a French image.
        new("explicit-locale.xml",
            InputFor(
                Options(new LocalAccountOptions { Name = null }, false, region: "fr-CH", keyboard: "fr-CH",
                    timezone: "Romance Standard Time"),
                "fr-FR"), null),

        // (f) Region given, keyboard on "ask": only the known values are written.
        new("region-only.xml",
            InputFor(Options(null, true, region: "en-GB", keyboard: "ask", timezone: "GMT Standard Time")), null),

        // (g) The other way round: keyboard given, region on "ask".
        new("keyboard-only.xml",
            InputFor(Options(null, false, region: "ask", keyboard: "auto", timezone: "ask"), machineScript: true), null)
    ];

    private static SetupOptions Options(LocalAccountOptions? account, bool skipPrivacy, string? computerName = null,
        string region = "auto", string keyboard = "auto", string timezone = "auto")
    {
        return new SetupOptions
        {
            LocalAccount = account,
            ComputerName = computerName,
            SkipPrivacyPages = skipPrivacy,
            BypassHardwareChecks = false,
            Region = region,
            Keyboard = keyboard,
            Timezone = timezone
        };
    }

    private static UnattendInput InputFor(SetupOptions options, string imageLanguage = "en-US",
        bool machineScript = false)
    {
        return UnattendInput.FromProfile(options, SetupResolver.Resolve(options, SwissHost, imageLanguage),
            machineScript);
    }

    public static TheoryData<string> CaseNames()
    {
        return [.. AllCases.Select(c => c.File)];
    }

    private static Case Find(string file)
    {
        return AllCases.Single(c => c.File == file);
    }

    private static XDocument Generate(string file)
    {
        var c = Find(file);
        return UnattendGenerator.Generate(c.Input, c.Password);
    }

    private static IEnumerable<XElement> Components(XDocument document)
    {
        return document.Descendants(Ns + "component");
    }

    private static XElement? Component(XDocument document, string pass, string name)
    {
        return document.Root!.Elements(Ns + "settings").SingleOrDefault(s => (string?)s.Attribute("pass") == pass)?
            .Elements(Ns + "component").SingleOrDefault(c => (string?)c.Attribute("name") == name);
    }

    // ---- expected files ------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Matches_the_expected_file(string file)
    {
        Expected.AssertMatches(file, UnattendGenerator.ToXmlString(Generate(file)));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Every_case_passes_validation_and_keeps_the_hard_rules(string file)
    {
        var document = Generate(file);

        Assert.Empty(UnattendGenerator.Validate(document));

        var names = document.Descendants().Select(e => e.Name.LocalName).ToHashSet();
        Assert.DoesNotContain("ProductKey", names);
        Assert.DoesNotContain("DiskConfiguration", names);
        Assert.DoesNotContain("InstallTo", names);
        Assert.DoesNotContain("InstallToAvailablePartition", names);
        Assert.DoesNotContain("WillWipeDisk", names);
        Assert.DoesNotContain("AutoLogon", names);

        const string expectedArchitecture = "amd64";
        Assert.NotEmpty(Components(document));
        var components = Components(document).ToList();
        Assert.All(components.Select(c => (string?)c.Attribute("processorArchitecture")),
            value => Assert.Equal(expectedArchitecture, value));
        Assert.All(components.Select(c => (string?)c.Attribute("publicKeyToken")),
            value => Assert.Equal("31bf3856ad364e35", value));
        Assert.All(components.Select(c => (string?)c.Attribute("language")), value => Assert.Equal("neutral", value));
        Assert.All(components.Select(c => (string?)c.Attribute("versionScope")),
            value => Assert.Equal("nonSxS", value));
        Assert.All(components.Select(c => c.Parent!.Name), name => Assert.Equal(Ns + "settings", name));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_text_is_stable_well_formed_utf8(string file)
    {
        var document = Generate(file);
        var text = UnattendGenerator.ToXmlString(document);
        var bytes = UnattendGenerator.ToXmlBytes(document);

        Assert.StartsWith(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<unattend xmlns=\"urn:schemas-microsoft-com:unattend\">\r\n",
            text);
        Assert.EndsWith("</unattend>\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.DoesNotContain("\t", text);

        // No byte order mark, and the bytes are the text.
        Assert.Equal((byte)'<', bytes[0]);
        Assert.Equal(text, new UTF8Encoding(false, true).GetString(bytes));

        // Same input, same text. The text also reads back as the same document.
        Assert.Equal(text, UnattendGenerator.ToXmlString(Generate(file)));
        Assert.Equal(text, UnattendGenerator.ToXmlString(XDocument.Parse(text)));
    }

    // ---- content by pass ------------------------------------------------------------------------

    [Fact]
    public void Setup_component_accepts_the_license_and_pins_image_index_1()
    {
        var setup = Component(Generate("minimal-ask.xml"), "windowsPE", "Microsoft-Windows-Setup")!;

        Assert.Equal("true", setup.Element(Ns + "UserData")!.Element(Ns + "AcceptEula")!.Value);
        var metaData =
            setup.Element(Ns + "ImageInstall")!.Element(Ns + "OSImage")!.Element(Ns + "InstallFrom")!.Element(
                Ns + "MetaData")!;
        Assert.Equal("add", (string?)metaData.Attribute(Wcm + "action"));
        Assert.Equal("/IMAGE/INDEX", metaData.Element(Ns + "Key")!.Value);
        Assert.Equal("1", metaData.Element(Ns + "Value")!.Value);
        Assert.Equal(["ImageInstall", "UserData"], setup.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["AcceptEula"], setup.Element(Ns + "UserData")!.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["InstallFrom"],
            setup.Element(Ns + "ImageInstall")!.Element(Ns + "OSImage")!.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void Locale_from_the_host_is_written_in_both_passes_with_the_image_language()
    {
        var document = Generate("recommended.xml");

        var winPe = Component(document, "windowsPE", "Microsoft-Windows-International-Core-WinPE")!;
        Assert.Equal("en-US", winPe.Element(Ns + "SetupUILanguage")!.Element(Ns + "UILanguage")!.Value);
        var oobe = Component(document, "oobeSystem", "Microsoft-Windows-International-Core")!;
        foreach (var component in new[] { winPe, oobe })
        {
            Assert.Equal("0807:00000807", component.Element(Ns + "InputLocale")!.Value);
            Assert.Equal("en-US", component.Element(Ns + "SystemLocale")!.Value);
            Assert.Equal("en-US", component.Element(Ns + "UILanguage")!.Value);
            Assert.Equal("de-CH", component.Element(Ns + "UserLocale")!.Value);
        }

        Assert.Equal("W. Europe Standard Time",
            Component(document, "specialize", "Microsoft-Windows-Shell-Setup")!.Element(Ns + "TimeZone")!.Value);
    }

    [Fact]
    public void Everything_on_ask_writes_no_international_component_and_no_specialize_pass()
    {
        var document = Generate("minimal-ask.xml");

        Assert.Equal(["windowsPE", "oobeSystem"], document.Root!.Elements().Select(s => (string?)s.Attribute("pass")));
        Assert.Equal(["Microsoft-Windows-Setup", "Microsoft-Windows-Shell-Setup"],
            Components(document).Select(c => (string?)c.Attribute("name")));
        var shell = Component(document, "oobeSystem", "Microsoft-Windows-Shell-Setup")!;
        Assert.Equal(["OOBE"], shell.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["HideEULAPage"], shell.Element(Ns + "OOBE")!.Elements().Select(e => e.Name.LocalName));
    }

    [Theory]
    [InlineData("region-only.xml", "UserLocale", "InputLocale")]
    [InlineData("keyboard-only.xml", "InputLocale", "UserLocale")]
    public void When_only_one_of_region_and_keyboard_is_known_the_other_element_is_left_out(string file, string present,
        string absent)
    {
        var document = Generate(file);

        foreach (var component in new[]
                 {
                     Component(document, "windowsPE", "Microsoft-Windows-International-Core-WinPE")!,
                     Component(document, "oobeSystem", "Microsoft-Windows-International-Core")!
                 })
        {
            Assert.NotNull(component.Element(Ns + present));
            Assert.Null(component.Element(Ns + absent));
            Assert.Equal("en-US", component.Element(Ns + "UILanguage")!.Value);
            Assert.Equal("en-US", component.Element(Ns + "SystemLocale")!.Value);
            // Left out, never written empty.
            Assert.DoesNotContain(component.Descendants(), e => e.Value.Length == 0);
        }
    }

    [Fact]
    public void Local_account_hides_the_online_account_pages_and_only_then()
    {
        Assert.Equal(
            ["HideEULAPage=true", "HideOnlineAccountScreens=true", "HideWirelessSetupInOOBE=true", "ProtectYourPC=3"],
            Oobe(Generate("recommended.xml")));
        Assert.Equal(["HideEULAPage=true", "HideOnlineAccountScreens=true", "HideWirelessSetupInOOBE=true"],
            Oobe(Generate("explicit-locale.xml")));
        Assert.Equal(["HideEULAPage=true", "ProtectYourPC=3"], Oobe(Generate("region-only.xml")));
        Assert.Equal(["HideEULAPage=true"], Oobe(Generate("minimal-ask.xml")));
        return;

        static string[] Oobe(XDocument d)
        {
            return
            [
                .. Component(d, "oobeSystem", "Microsoft-Windows-Shell-Setup")!.Element(Ns + "OOBE")!.Elements()
                    .Select(e => e.Name.LocalName + "=" + e.Value)
            ];
        }
    }

    [Fact]
    public void No_account_is_created_when_setup_asks_for_the_name()
    {
        Assert.Empty(Generate("recommended.xml").Descendants(Ns + "UserAccounts"));
        Assert.Empty(Generate("minimal-ask.xml").Descendants(Ns + "UserAccounts"));
    }

    [Fact]
    public void Named_account_without_password_is_an_administrator_with_an_empty_plain_text_password()
    {
        var document = Generate("named-account.xml");
        var account = document.Descendants(Ns + "LocalAccount").Single();

        Assert.Equal("add", (string?)account.Attribute(Wcm + "action"));
        Assert.Equal("Anna", account.Element(Ns + "Name")!.Value);
        Assert.Equal("Anna", account.Element(Ns + "DisplayName")!.Value);
        Assert.Equal("Administrators", account.Element(Ns + "Group")!.Value);
        var password = account.Element(Ns + "Password")!;
        Assert.Equal("", password.Element(Ns + "Value")!.Value);
        Assert.Equal("true", password.Element(Ns + "PlainText")!.Value);
        Assert.Contains("<Value></Value>", UnattendGenerator.ToXmlString(document));

        Assert.Equal("ANNA-LAPTOP",
            Component(document, "specialize", "Microsoft-Windows-Shell-Setup")!.Element(Ns + "ComputerName")!.Value);
    }

    [Fact]
    public void Password_is_written_in_the_hidden_form_windows_expects()
    {
        var password = Generate("named-account-password.xml").Descendants(Ns + "Password").Single();

        Assert.Equal("false", password.Element(Ns + "PlainText")!.Value);
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(password.Element(Ns + "Value")!.Value));
        Assert.Equal(TestPassword + "Password", decoded);
    }

    [Fact]
    public void Password_encoding_matches_the_example_in_microsofts_documentation()
    {
        // Microsoft Learn, LocalAccount/Password: the second sample value is the password "password".
        Assert.Equal("cABhAHMAcwB3AG8AcgBkAFAAYQBzAHMAdwBvAHIAZAA=", UnattendGenerator.EncodePassword("password"));
        // The value Windows System Image Manager writes for an empty hidden password.
        Assert.Equal("UABhAHMAcwB3AG8AcgBkAA==", UnattendGenerator.EncodePassword(""));
    }

    [Fact]
    public void First_logon_command_is_present_only_when_there_is_a_machine_script()
    {
        var with = Generate("named-account-password.xml").Descendants(Ns + "FirstLogonCommands").Single();
        var command = with.Elements(Ns + "SynchronousCommand").Single();
        Assert.Equal("add", (string?)command.Attribute(Wcm + "action"));
        Assert.Equal("1", command.Element(Ns + "Order")!.Value);
        Assert.Equal(FirstLogonScriptGenerator.MachineCommandLine, command.Element(Ns + "CommandLine")!.Value);
        Assert.False(string.IsNullOrWhiteSpace(command.Element(Ns + "Description")!.Value));

        Assert.Empty(Generate("recommended.xml").Descendants(Ns + "FirstLogonCommands"));
        Assert.Empty(Generate("named-account.xml").Descendants(Ns + "FirstLogonCommands"));
    }

    [Fact]
    public void Names_with_xml_characters_survive_unchanged()
    {
        var options = Options(new LocalAccountOptions { Name = "Tom & Jerry's PC" }, false);

        var document = UnattendGenerator.Generate(InputFor(options));
        var text = UnattendGenerator.ToXmlString(document);

        Assert.Contains("<Name>Tom &amp; Jerry's PC</Name>", text);
        Assert.Equal("Tom & Jerry's PC",
            XDocument.Parse(text).Descendants(Ns + "LocalAccount").Single().Element(Ns + "Name")!.Value);
    }

    // ---- the password stays out of logs ---------------------------------------------------------

    [Fact]
    public void Log_safe_text_never_contains_the_password()
    {
        var document = Generate("named-account-password.xml");
        var encoded = UnattendGenerator.EncodePassword(TestPassword);
        var before = UnattendGenerator.ToXmlString(document);

        var safe = UnattendGenerator.ToLogSafeString(document);

        Assert.Contains(encoded, before);
        Assert.DoesNotContain(TestPassword, before);
        Assert.DoesNotContain(encoded, safe);
        Assert.DoesNotContain(TestPassword, safe);
        Assert.DoesNotContain("Correct horse", safe);
        Assert.Contains($"<Value>{UnattendGenerator.PasswordMask}</Value>", safe);
        // Nothing else is touched: the image index is also a <Value>.
        Assert.Contains("<Value>1</Value>", safe);
        // The document itself is not changed by producing the log text.
        Assert.Equal(before, UnattendGenerator.ToXmlString(document));
        // Apart from the one masked value the two texts are the same.
        Assert.Equal(before.Replace(encoded, UnattendGenerator.PasswordMask, StringComparison.Ordinal), safe);
    }

    [Fact]
    public void Log_safe_text_masks_a_plain_text_password_too()
    {
        // Not something the generator writes, but the mask must not depend on that.
        var document = Generate("named-account.xml");
        var password = document.Descendants(Ns + "Password").Single();
        password.Element(Ns + "Value")!.Value = "hunter2";

        var safe = UnattendGenerator.ToLogSafeString(document);

        Assert.DoesNotContain("hunter2", safe);
        Assert.Contains(UnattendGenerator.PasswordMask, safe);
    }

    [Fact]
    public void Log_safe_text_of_a_file_without_password_is_the_file()
    {
        var document = Generate("recommended.xml");

        Assert.Equal(UnattendGenerator.ToXmlString(document), UnattendGenerator.ToLogSafeString(document));
    }

    [Fact]
    public void The_input_record_cannot_carry_a_password()
    {
        var c = Find("named-account-password.xml");

        Assert.DoesNotContain(TestPassword, c.Input.ToString());
        Assert.DoesNotContain("Correct horse", c.Input.ToString());
        Assert.DoesNotContain(typeof(UnattendInput).GetProperties(),
            p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    // ---- refused input ---------------------------------------------------------------------------

    [Theory]
    [InlineData("Administrator")]
    [InlineData("administrator")]
    [InlineData("Guest")]
    [InlineData("Users")]
    [InlineData("defaultuser0")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" Anna")]
    [InlineData("Anna ")]
    [InlineData("Anna.")]
    [InlineData("An/na")]
    [InlineData("An\\na")]
    [InlineData("Anna@home")]
    [InlineData("Anna<script>")]
    [InlineData("Anna\n")]
    [InlineData("Anna\tB")]
    [InlineData("A name that is far too long")]
    public void Refuses_account_names_windows_would_reject(string name)
    {
        var options = Options(new LocalAccountOptions { Name = name }, false);

        var ex = Assert.Throws<PlainstallException>(() => UnattendGenerator.Generate(InputFor(options)));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("MY PC")]
    [InlineData("MY_PC")]
    [InlineData("SIXTEEN-CHARS-PC")]
    [InlineData("PC\n")]
    [InlineData("PC<1>")]
    public void Refuses_computer_names_windows_would_reject(string name)
    {
        var options = Options(null, false, name);

        var ex = Assert.Throws<PlainstallException>(() => UnattendGenerator.Generate(InputFor(options)));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
    }

    [Fact]
    public void Refuses_the_same_name_for_account_and_computer()
    {
        var options = Options(new LocalAccountOptions { Name = "anna" }, false, "ANNA");

        var ex = Assert.Throws<PlainstallException>(() => UnattendGenerator.Generate(InputFor(options)));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
    }

    [Fact]
    public void Refuses_a_password_when_no_account_is_named()
    {
        var askName = InputFor(Options(new LocalAccountOptions { Name = null }, false));
        var noAccount = InputFor(Options(null, false));

        Assert.Equal(ErrorKind.InvalidInput,
            Assert.Throws<PlainstallException>(() => UnattendGenerator.Generate(askName, "secret")).Kind);
        Assert.Equal(ErrorKind.InvalidInput,
            Assert.Throws<PlainstallException>(() => UnattendGenerator.Generate(noAccount, "secret")).Kind);
    }

    [Fact]
    public void Refuses_to_create_an_open_account_when_the_profile_asked_for_a_password()
    {
        var input = InputFor(Options(new LocalAccountOptions { Name = "Anna", AskPassword = true }, false));

        var ex = Assert.Throws<PlainstallException>(() => UnattendGenerator.Generate(input));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        // An empty string is the host saying "the user chose no password", which is allowed.
        var document = UnattendGenerator.Generate(input, "");
        Assert.Equal("true", document.Descendants(Ns + "PlainText").Single().Value);
    }

    [Fact]
    public void Refuses_a_password_that_is_too_long_without_repeating_it()
    {
        var input = InputFor(Options(new LocalAccountOptions { Name = "Anna", AskPassword = true }, false));
        var tooLong = new string('x', UnattendGenerator.MaxPasswordLength + 1);

        var ex = Assert.Throws<PlainstallException>(() => UnattendGenerator.Generate(input, tooLong));

        Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
        Assert.DoesNotContain("xxxx", ex.Message);
        Assert.NotNull(UnattendGenerator.Generate(input, new string('x', UnattendGenerator.MaxPasswordLength)));
    }

    [Fact]
    public void Refuses_resolved_values_that_are_not_what_they_claim_to_be()
    {
        var good = Find("recommended.xml").Input;

        foreach (var bad in new[]
                 {
                     good with { Setup = good.Setup with { UserLocale = "ask" } },
                     good with { Setup = good.Setup with { UserLocale = "de-CH</UserLocale>" } },
                     good with { Setup = good.Setup with { InputLocale = "qwertz" } },
                     good with { Setup = good.Setup with { UiLanguage = "English" } },
                     good with { Setup = good.Setup with { TimeZone = "line\nbreak" } }
                 })
            Assert.Equal(ErrorKind.InvalidInput,
                Assert.Throws<PlainstallException>(() => UnattendGenerator.Generate(bad)).Kind);
        Assert.Equal(ErrorKind.Internal,
            Assert.Throws<PlainstallException>(() => UnattendGenerator.Generate(good with { ImageIndex = 0 })).Kind);
    }

    // ---- Validate: negative cases ----------------------------------------------------------------

    private static XDocument Valid()
    {
        return Generate("named-account-password.xml");
    }

    private static void AssertProblem(XDocument document, string expectedPart)
    {
        var problems = UnattendGenerator.Validate(document);
        Assert.Contains(problems, p => p.Contains(expectedPart, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_accepts_what_generate_produces()
    {
        Assert.Empty(UnattendGenerator.Validate(Valid()));
    }

    [Fact]
    public void Validate_reports_a_wrong_root()
    {
        AssertProblem(new XDocument(new XElement("unattend")), "root element");
        AssertProblem(new XDocument(new XElement(Ns + "answers")), "root element");
    }

    [Theory]
    [InlineData("ProductKey")]
    [InlineData("DiskConfiguration")]
    [InlineData("InstallTo")]
    [InlineData("InstallToAvailablePartition")]
    [InlineData("AutoLogon")]
    public void Validate_reports_forbidden_elements_wherever_they_are(string name)
    {
        var inUserData = Valid();
        inUserData.Descendants(Ns + "UserData").Single().Add(new XElement(Ns + name));
        AssertProblem(inUserData, $"<{name}> found");

        // Also when it is nested somewhere else, or sits in no namespace at all.
        var elsewhere = Valid();
        elsewhere.Descendants(Ns + "OOBE").Single().Add(new XElement(Ns + "Wrapper", new XElement(name)));
        AssertProblem(elsewhere, $"<{name}> found");

        // The virtual machine test allows each of these elements, but not ProductKey.
        Assert.Equal(name != "ProductKey", UnattendGenerator.Validate(inUserData, true).Count == 0);
    }

    [Fact]
    public void Validate_reports_a_component_outside_a_settings_pass()
    {
        var atRoot = Valid();
        var component = atRoot.Descendants(Ns + "component").First();
        component.Remove();
        atRoot.Root!.Add(component);
        AssertProblem(atRoot, "is not directly inside a <settings");
        AssertProblem(atRoot, "Only <settings> is");

        var nested = Valid();
        var outer = nested.Descendants(Ns + "component").First();
        outer.Add(new XElement(outer.Name, outer.Attributes()));
        AssertProblem(nested, "is not directly inside a <settings");
    }

    [Theory]
    [InlineData("oobe")]
    [InlineData("OobeSystem")]
    [InlineData("")]
    [InlineData("windowsPE ")]
    public void Validate_reports_an_unknown_pass_name(string pass)
    {
        var document = Valid();
        document.Root!.Elements(Ns + "settings").Last().SetAttributeValue("pass", pass);

        AssertProblem(document, "does not name a configuration pass");
    }

    [Fact]
    public void Validate_reports_a_missing_pass_attribute_and_a_repeated_pass()
    {
        var missing = Valid();
        missing.Root!.Elements(Ns + "settings").First().Attribute("pass")!.Remove();
        AssertProblem(missing, "does not name a configuration pass");

        var repeated = Valid();
        repeated.Root!.Elements(Ns + "settings").Last().SetAttributeValue("pass", "windowsPE");
        AssertProblem(repeated, "appears more than once");
    }

    [Theory]
    [InlineData("processorArchitecture", "has no processorArchitecture attribute")]
    [InlineData("publicKeyToken", "has no publicKeyToken attribute")]
    [InlineData("language", "has no language attribute")]
    [InlineData("versionScope", "has no versionScope attribute")]
    [InlineData("name", "has no name attribute")]
    public void Validate_reports_a_missing_component_attribute(string attribute, string expectedPart)
    {
        var document = Valid();
        document.Descendants(Ns + "component").Last().Attribute(attribute)!.Remove();

        AssertProblem(document, expectedPart);
    }

    [Theory]
    [InlineData("publicKeyToken", "31BF3856AD364E35")]
    [InlineData("language", "en-US")]
    [InlineData("versionScope", "SxS")]
    public void Validate_reports_a_wrong_component_attribute(string attribute, string value)
    {
        var document = Valid();
        document.Descendants(Ns + "component").First().SetAttributeValue(attribute, value);

        AssertProblem(document, $"has {attribute}=\"{value}\"");
    }

    [Fact]
    public void Validate_reports_an_architecture_that_does_not_match_the_image()
    {
        // A whole file written for another architecture: every component is reported.
        var foreign = Valid();
        foreach (var component in foreign.Descendants(Ns + "component"))
            component.SetAttributeValue("processorArchitecture", "arm64");
        var problems = UnattendGenerator.Validate(foreign);
        Assert.Equal(Valid().Descendants(Ns + "component").Count(),
            problems.Count(p => p.Contains("is for arm64, but the image is amd64")));

        // One stray component.
        var mixed = Valid();
        mixed.Descendants(Ns + "component").Last().SetAttributeValue("processorArchitecture", "x86");
        AssertProblem(mixed, "is for x86, but the image is amd64");
    }

    [Fact]
    public void Validate_reports_elements_outside_the_unattend_namespace()
    {
        var document = Valid();
        document.Descendants(Ns + "OOBE").Single().Add(new XElement("HideEULAPage", "true"));

        AssertProblem(document, "is not in the namespace");
    }

    [Fact]
    public void Validate_reports_a_component_listed_twice_in_one_pass()
    {
        var document = Valid();
        var component = document.Descendants(Ns + "component").Last();
        component.AddAfterSelf(new XElement(component));

        AssertProblem(document, "appears more than once in the pass oobeSystem");
    }

    [Fact]
    public void Validate_reports_stray_elements_inside_a_settings_pass()
    {
        var document = Valid();
        document.Root!.Elements(Ns + "settings").First().Add(new XElement(Ns + "UserData"));

        AssertProblem(document, "Only <component> is");
    }

    // ---- nothing changes while the virtual machine test mode is off -----------------------------

    /// <summary>
    ///     SHA-256 of the bytes of each ordinary expected answer file. The generator must always produce
    ///     exactly these bytes.
    /// </summary>
    [Theory]
    [InlineData("explicit-locale.xml", "ECE8F91C66726797627B9E054582779A1C9A966C7BCF944E1B2F25373F611F08")]
    [InlineData("keyboard-only.xml", "9562750451558EC8B5CC29946CBCC52DF158A24F7CA58628407FD2A6AE3E6D82")]
    [InlineData("minimal-ask.xml", "490062B7C1BD60E490CFA0D9F5BEACC8B114FDCF0A7215C5A0A81889FD96BAF7")]
    [InlineData("named-account.xml", "D1AC154CE71B0E21A4B0A9F9E5A6F9BFAB2721DA120F0F1D96C5F298331F7094")]
    [InlineData("named-account-password.xml", "FCB72F392994F1C8324E1B73FC93350D7315AD8915363C31729D11CC7A705565")]
    [InlineData("recommended.xml", "353000E13108D0B578F9EE754DC3CC7268C0DB3051C4A1CDDBDECA6B9601FA8B")]
    [InlineData("region-only.xml", "C2ABDC0308ACEEA3C305AA6D51EC099FF29CEBB3301EE52254DB799647CC1BF2")]
    public void Without_the_vm_test_flag_every_file_is_byte_identical_to_before(string file, string sha256)
    {
        var c = Find(file);
        Assert.False(c.Input.VmTest);

        var bytes = UnattendGenerator.ToXmlBytes(UnattendGenerator.Generate(c.Input, c.Password));

        Assert.Equal(sha256, HashHex.Sha256Of(bytes));
        // Setting the flag to false by hand is the same as not setting it.
        Assert.Equal(bytes,
            UnattendGenerator.ToXmlBytes(UnattendGenerator.Generate(c.Input with { VmTest = false }, c.Password)));
    }

    [Fact]
    public void The_pinned_hashes_cover_every_ordinary_case()
    {
        var pinned = typeof(UnattendGeneratorTests)
            .GetMethod(nameof(Without_the_vm_test_flag_every_file_is_byte_identical_to_before))!
            .GetCustomAttributesData()
            .Count(a => a.AttributeType == typeof(InlineDataAttribute));

        Assert.Equal(AllCases.Length, pinned);
    }

    [Fact]
    public void From_profile_never_switches_the_vm_test_mode_on()
    {
        Assert.All(AllCases, c => Assert.False(c.Input.VmTest));
        Assert.False(new UnattendInput
        {
            Setup = Find("minimal-ask.xml").Input.Setup,
            LocalAccount = null,
            SkipPrivacyPages = false,
            HasMachineFirstLogonScript = false
        }.VmTest);
    }

    /// <summary>x64, a named account with a password, first sign-in work: what the Hyper-V harness builds.</summary>
    private static UnattendInput VmTestInput()
    {
        return InputFor(Options(new LocalAccountOptions { Name = "Tester", AskPassword = true }, true, "PLAINSTALL-VM"),
                machineScript: true)
            with
            {
                VmTest = true
            };
    }

    private static XDocument VmTestDocument()
    {
        return UnattendGenerator.Generate(VmTestInput(), TestPassword);
    }

    [Fact]
    public void Vm_test_file_matches_the_expected_file()
    {
        Expected.AssertMatches(VmTestFile, UnattendGenerator.ToXmlString(VmTestDocument()));
    }

    [Fact]
    public void Vm_test_file_is_accepted_only_when_the_caller_allows_it()
    {
        var document = VmTestDocument();

        Assert.Empty(UnattendGenerator.Validate(document, true));

        var problems = UnattendGenerator.Validate(document);
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("<DiskConfiguration> found", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("<InstallTo> found", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("<AutoLogon> found", StringComparison.Ordinal));
    }

    [Fact]
    public void A_product_key_is_rejected_in_vm_test_mode_too()
    {
        var document = VmTestDocument();
        Assert.DoesNotContain(document.Descendants(), e => e.Name.LocalName == "ProductKey");

        document.Descendants(Ns + "UserData").Single()
            .Add(new XElement(Ns + "ProductKey", new XElement(Ns + "WillShowUI", "OnError")));

        Assert.Contains(UnattendGenerator.Validate(document, true),
            p => p.StartsWith("<ProductKey> found", StringComparison.Ordinal));
        Assert.Contains(UnattendGenerator.Validate(document),
            p => p.StartsWith("<ProductKey> found", StringComparison.Ordinal));
    }

    [Fact]
    public void The_other_rules_still_apply_in_vm_test_mode()
    {
        var document = VmTestDocument();

        var stray = VmTestDocument();
        stray.Descendants(Ns + "component").Last().SetAttributeValue("processorArchitecture", "arm64");
        Assert.Contains(UnattendGenerator.Validate(stray, true),
            p => p.Contains("is for arm64, but the image is amd64"));
        document.Descendants(Ns + "component").First().Attribute("versionScope")!.Remove();
        Assert.Contains(UnattendGenerator.Validate(document, true), p => p.Contains("has no versionScope attribute"));
    }

    [Fact]
    public void Vm_test_needs_a_named_local_account()
    {
        var noAccount = InputFor(Options(null, true)) with { VmTest = true };
        var nameAskedBySetup = InputFor(Options(new LocalAccountOptions { Name = null }, true)) with { VmTest = true };

        foreach (var input in new[] { noAccount, nameAskedBySetup })
        {
            var ex = Assert.Throws<PlainstallException>(() => UnattendGenerator.Generate(input));
            Assert.Equal(ErrorKind.InvalidInput, ex.Kind);
            Assert.Contains("virtual machine test", ex.Message);
        }

        // The same inputs are fine for a normal build.
        Assert.NotNull(UnattendGenerator.Generate(noAccount with { VmTest = false }));
        Assert.NotNull(UnattendGenerator.Generate(nameAskedBySetup with { VmTest = false }));
    }

    [Fact]
    public void Vm_test_file_says_at_the_top_that_it_erases_disk_0()
    {
        var document = VmTestDocument();
        var comments = document.Root!.Nodes().TakeWhile(n => n is XComment).Cast<XComment>().Select(c => c.Value)
            .ToList();

        Assert.Equal(2, comments.Count);
        Assert.Contains("VIRTUAL MACHINE TEST MEDIA", comments[1]);
        Assert.Contains("erases disk 0", comments[1]);
        Assert.Contains("without asking", comments[1]);
        Assert.Contains("Never boot a real PC from it", comments[1]);
        // The promise of the normal file would be false here, so it is not made.
        Assert.DoesNotContain(comments, c => c.Contains("never selects or erases", StringComparison.OrdinalIgnoreCase));

        // A normal file has the one comment, with the promise.
        var normal = Generate("named-account-password.xml").Root!.Nodes().OfType<XComment>().Select(c => c.Value)
            .ToList();
        Assert.Contains("It never selects or erases a disk.", Assert.Single(normal));
    }

    [Fact]
    public void Vm_test_wipes_disk_0_into_efi_msr_and_windows_and_installs_to_the_windows_partition()
    {
        var setup = Component(VmTestDocument(), "windowsPE", "Microsoft-Windows-Setup")!;
        var configuration = setup.Element(Ns + "DiskConfiguration")!;

        Assert.Equal("OnError", configuration.Element(Ns + "WillShowUI")!.Value);
        var disk = Assert.Single(configuration.Elements(Ns + "Disk"));
        Assert.Equal("add", (string?)disk.Attribute(Wcm + "action"));
        Assert.Equal("0", disk.Element(Ns + "DiskID")!.Value);
        Assert.Equal("true", disk.Element(Ns + "WillWipeDisk")!.Value);

        var created = disk.Element(Ns + "CreatePartitions")!.Elements(Ns + "CreatePartition").ToList();
        Assert.Equal(["Order=1 Type=EFI Size=300", "Order=2 Type=MSR Size=16", "Order=3 Type=Primary Extend=true"],
            created.Select(Describe).ToArray());
        var modified = disk.Element(Ns + "ModifyPartitions")!.Elements(Ns + "ModifyPartition").ToList();
        Assert.Equal(
            [
                "Order=1 PartitionID=1 Label=System Format=FAT32",
                "Order=2 PartitionID=3 Label=Windows Letter=C Format=NTFS"
            ],
            modified.Select(Describe).ToArray());
        Assert.All(created.Concat(modified), e => Assert.Equal("add", (string?)e.Attribute(Wcm + "action")));

        var image = setup.Element(Ns + "ImageInstall")!.Element(Ns + "OSImage")!;
        Assert.Equal("DiskID=0 PartitionID=3", Describe(image.Element(Ns + "InstallTo")!));
        // The image is still chosen by index, and the license is still accepted.
        Assert.Equal("/IMAGE/INDEX",
            image.Element(Ns + "InstallFrom")!.Element(Ns + "MetaData")!.Element(Ns + "Key")!.Value);
        Assert.Equal("true", setup.Element(Ns + "UserData")!.Element(Ns + "AcceptEula")!.Value);
        Assert.Empty(setup.Descendants(Ns + "InstallToAvailablePartition"));
        return;

        static string Describe(XElement e)
        {
            return string.Join(" ", e.Elements().Select(c => c.Name.LocalName + "=" + c.Value));
        }
    }

    [Fact]
    public void Vm_test_signs_the_created_account_in_once_with_the_same_password_form()
    {
        var shell = Component(VmTestDocument(), "oobeSystem", "Microsoft-Windows-Shell-Setup")!;
        var autoLogon = shell.Element(Ns + "AutoLogon")!;
        var account = shell.Descendants(Ns + "LocalAccount").Single();

        Assert.Equal("true", autoLogon.Element(Ns + "Enabled")!.Value);
        Assert.Equal("1", autoLogon.Element(Ns + "LogonCount")!.Value);
        Assert.Equal("Tester", autoLogon.Element(Ns + "Username")!.Value);
        Assert.Equal(account.Element(Ns + "Name")!.Value, autoLogon.Element(Ns + "Username")!.Value);
        Assert.Null(autoLogon.Element(Ns + "Domain"));
        Assert.True(XNode.DeepEquals(account.Element(Ns + "Password"), autoLogon.Element(Ns + "Password")));
        Assert.Equal("false", autoLogon.Element(Ns + "Password")!.Element(Ns + "PlainText")!.Value);
        Assert.Equal(UnattendGenerator.EncodePassword(TestPassword),
            autoLogon.Element(Ns + "Password")!.Element(Ns + "Value")!.Value);

        // Without a password both places get the empty plain-text form.
        var open = UnattendGenerator.Generate(VmTestInput() with
        {
            LocalAccount = new LocalAccountOptions { Name = "Tester" }
        });
        var passwords = open.Descendants(Ns + "Password").ToList();
        Assert.Equal(2, passwords.Count);
        Assert.All(passwords.Select(p => p.Element(Ns + "Value")!.Value), value => Assert.Equal("", value));
        Assert.All(passwords.Select(p => p.Element(Ns + "PlainText")!.Value),
            plainText => Assert.Equal("true", plainText));
    }

    [Fact]
    public void Vm_test_log_text_hides_the_password_in_both_places()
    {
        var document = VmTestDocument();
        var encoded = UnattendGenerator.EncodePassword(TestPassword);

        var safe = UnattendGenerator.ToLogSafeString(document);

        Assert.Equal(2, Regex.Count(UnattendGenerator.ToXmlString(document), Regex.Escape(encoded)));
        Assert.DoesNotContain(encoded, safe);
        Assert.DoesNotContain(TestPassword, safe);
        Assert.Equal(2, Regex.Count(safe, Regex.Escape($"<Value>{UnattendGenerator.PasswordMask}</Value>")));
    }

    [Fact]
    public void Vm_test_changes_nothing_but_the_disk_the_sign_in_and_the_comments()
    {
        var vm = VmTestDocument();
        var normal = UnattendGenerator.Generate(VmTestInput() with { VmTest = false }, TestPassword);

        vm.Descendants(Ns + "DiskConfiguration").Remove();
        vm.Descendants(Ns + "InstallTo").Remove();
        vm.Descendants(Ns + "AutoLogon").Remove();
        vm.Root!.Nodes().OfType<XComment>().Remove();
        normal.Root!.Nodes().OfType<XComment>().Remove();

        Assert.Equal(UnattendGenerator.ToXmlString(normal), UnattendGenerator.ToXmlString(vm));
    }

    /// <summary>One expected-file case: its file name, the input, and the password given at build time.</summary>
    public sealed record Case(string File, UnattendInput Input, string? Password)
    {
        public override string ToString()
        {
            return File;
        }
    }
}