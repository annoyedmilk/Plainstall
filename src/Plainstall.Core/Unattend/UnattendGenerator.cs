using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Plainstall.Core.Profiles;

namespace Plainstall.Core.Unattend;

/// <summary>
///     Everything <see cref="UnattendGenerator" /> needs. The account password is deliberately not part of
///     this record: it is passed as its own argument, so that logging or comparing the input can never
///     expose it.
/// </summary>
public sealed record UnattendInput
{
    /// <summary>
    ///     Index of the image inside install.wim. Always 1, because the build exports the one chosen
    ///     edition into a new file. It is still written, so that Setup has nothing left to choose and
    ///     no product key is needed to pick an edition.
    /// </summary>
    public int ImageIndex { get; init; } = 1;

    public required ResolvedSetup Setup { get; init; }

    /// <summary>
    ///     Null: Setup runs its normal Microsoft account pages. Not null: local account. With a null
    ///     <see cref="LocalAccountOptions.Name" /> Setup asks for the name. With a name the account is created.
    /// </summary>
    public required LocalAccountOptions? LocalAccount { get; init; }

    /// <summary>Null: Windows picks a random name.</summary>
    public string? ComputerName { get; init; }

    public required bool SkipPrivacyPages { get; init; }

    /// <summary>True when Machine.ps1 was written into the image and must be started at the first sign-in.</summary>
    public required bool HasMachineFirstLogonScript { get; init; }

    /// <summary>
    ///     <b>For the automated virtual machine test only.</b> False for everything a user builds.
    ///     <para>
    ///         True makes the answer file do the two things Plainstall otherwise never does, so that an
    ///         installation reaches the desktop with nobody at the keyboard: it erases disk 0 and partitions it
    ///         without asking, and it signs the created account in automatically once. Media built this way
    ///         destroys the data of any PC that boots from it. It needs a named local account.
    ///     </para>
    ///     <para>
    ///         It is not part of a profile and <see cref="FromProfile" /> never sets it. The test harness
    ///         switches it on explicitly.
    ///     </para>
    /// </summary>
    public bool VmTest { get; init; }

    /// <summary>The usual way to build the input: from a profile's Setup options.</summary>
    public static UnattendInput FromProfile(SetupOptions options, ResolvedSetup setup, bool hasMachineFirstLogonScript)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new UnattendInput
        {
            Setup = setup,
            LocalAccount = options.LocalAccount,
            ComputerName = options.ComputerName,
            SkipPrivacyPages = options.SkipPrivacyPages,
            HasMachineFirstLogonScript = hasMachineFirstLogonScript
        };
    }
}

/// <summary>
///     Builds <c>autounattend.xml</c> as an <see cref="XDocument" />. There is no template file. This class
///     makes each element, so the file contains only what this class decides.
///     <para>
///         <b>What the file never contains</b>
///     </para>
///     <list type="bullet">
///         <item>
///             No <c>DiskConfiguration</c> and no <c>InstallTo</c>. The user always selects the disk and the
///             partition in Setup. To erase "disk 0" without a question is the worst thing that this tool can
///             do to a beginner.
///         </item>
///         <item>
///             No <c>ProductKey</c> element, also no empty one. Other tools tried each form of that element
///             without a key (an all-zero key, <c>WillShowUI</c> alone). Each form stopped Setup on some PCs with
///             a product key error. The edition is set without it: the image file holds one edition, the image
///             index is fixed, and ei.cfg names the edition.
///         </item>
///         <item>No <c>AutoLogon</c>. See below.</item>
///     </list>
///     <para>
///         The only exception to the first and the third rule is <see cref="UnattendInput.VmTest" />. It is
///         for the automated virtual machine test. The last section tells more.
///     </para>
///     <para>
///         <b>Why there is no AutoLogon</b>
///     </para>
///     <para>
///         Microsoft documents <c>FirstLogonCommands</c> as commands that run "the first time a user logs on
///         to the computer", once, "after logon, prior to showing the desktop". They run with elevated rights
///         when that user is an administrator. The first interactive sign-in of any user starts them, in any
///         way. Other generators add <c>AutoLogon</c> only so that an installation finishes when nobody is at
///         the PC. The Plainstall user is at the PC and signs in: with the password, or with no prompt when the
///         account has no password. Thus <c>AutoLogon</c> gives nothing. Also, it would keep the password a
///         second time, in readable form, in the registry of the installed PC.
///     </para>
///     <para>
///         Here the first account is always an administrator. This file adds it to the Administrators group,
///         or the account page of Setup makes it. Thus the documented case "a user without administrative
///         privileges signs in first and is asked for an administrator" does not occur.
///     </para>
///     <para>
///         <b>An account without a password</b>
///     </para>
///     <para>
///         When the profile gives an account name and no password, the account gets an empty password:
///         <c>&lt;Value&gt;&lt;/Value&gt;</c> with <c>PlainText</c> true. Without the <c>Password</c> element,
///         Setup would decide. With it, the intent is clear. Windows then signs that account in without a
///         question. That is also an interactive sign-in, so the first-logon work runs. By default, Windows
///         lets an account with an empty password sign in only at the PC, not through the network (the policy
///         "Limit local account use of blank passwords to console logon only").
///     </para>
///     <para>
///         <b>How a password is written</b>
///     </para>
///     <para>
///         With <c>PlainText</c> false, in the form that Windows System Image Manager makes: Base64 of the
///         UTF-16LE bytes of the password, followed by the word "Password". This is an encoding, not
///         encryption. Each person who has the stick can read it, and the app says so. Plainstall uses it
///         instead of plain text for two reasons. Nobody can read the password over the shoulder of the user.
///         Spaces at the start or the end stay, and XML text does not make sure of that.
///     </para>
///     <para>
///         <b>Region and keyboard</b>
///     </para>
///     <para>
///         The international components are written when region or keyboard (or both) is resolved. They hold
///         only the resolved values: <c>UILanguage</c> and <c>SystemLocale</c> (the language of the image),
///         plus <c>UserLocale</c>, <c>InputLocale</c> or both. When a value is "ask", its element is not
///         written, and never written empty. Setup skips its regional pages only when it has all values.
///         Thus it shows the page for the missing value. When both values are "ask", no component is written.
///         There is one documented exception in the windowsPE pass. When Setup finds an <c>autounattend.xml</c>
///         itself, it does not show its language page, also when the file has no language settings. The
///         region and keyboard pages after the installation (OOBE) are the pages that always follow "ask".
///     </para>
///     <para>
///         <b>Virtual machine test mode</b>
///     </para>
///     <para>
///         With <see cref="UnattendInput.VmTest" />, the file also holds three more parts. A
///         <c>DiskConfiguration</c> erases disk 0 and makes the UEFI/GPT layout from the answer-file reference
///         of Microsoft (EFI system partition, Microsoft reserved partition, Windows partition on the rest).
///         An <c>InstallTo</c> selects the Windows partition. An <c>AutoLogon</c> signs in the new account one
///         time. A comment at the top of the file tells what the file does. <see cref="Validate" /> accepts
///         such a file only when the caller asks for it by name. Thus a normal build cannot write one by
///         mistake. The product key rule has no exception.
///     </para>
/// </summary>
public static partial class UnattendGenerator
{
    /// <summary>The name Windows Setup searches for in the root of the stick.</summary>
    public const string FileName = "autounattend.xml";

    /// <summary>What <see cref="ToLogSafeString" /> writes in place of a password value.</summary>
    public const string PasswordMask = "********";

    public const string PublicKeyToken = "31bf3856ad364e35";

    /// <summary>What every component names as its processor architecture: Plainstall builds x64 images only.</summary>
    public const string ProcessorArchitecture = "amd64";

    /// <summary>Longest password a Windows local account takes.</summary>
    public const int MaxPasswordLength = 127;

    private const string PassWindowsPe = "windowsPE";
    private const string PassSpecialize = "specialize";
    private const string PassOobeSystem = "oobeSystem";

    // Sizes in MB, from Microsoft's "UEFI/GPT-based hard drive partitions": the EFI system partition
    // needs at least 200 MB, or 300 MB on a drive with 4K native sectors, so 300 fits both. The
    // Microsoft reserved partition is 16 MB.
    private const string VmTestEfiSizeMb = "300";
    private const string VmTestMsrSizeMb = "16";
    private const string VmTestDiskId = "0";

    // Partitions are numbered in the order they are created: EFI 1, MSR 2, Windows 3.
    private const string VmTestEfiPartitionId = "1";
    private const string VmTestWindowsPartitionId = "3";

    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly string[] ValidPasses =
        ["windowsPE", "offlineServicing", "generalize", "specialize", "auditSystem", "auditUser", "oobeSystem"];

    /// <summary>Elements that must never appear, in any mode, with the reason shown when they do.</summary>
    private static readonly (string Name, string Why)[] AlwaysForbiddenElements =
    [
        ("ProductKey",
            "the answer file must not contain a ProductKey element, with or without a key. Keyless forms of it have stopped Setup with a product key error. The single-edition image with its pinned index needs none")
    ];

    /// <summary>
    ///     Elements that only the virtual machine test may contain. In a file for a real PC each of them is
    ///     a way to lose data or to weaken the account, so they are rejected unless the caller allows them.
    /// </summary>
    private static readonly (string Name, string Why)[] VmTestOnlyElements =
    [
        ("DiskConfiguration",
            "the answer file must not partition a disk. The user picks the disk in Setup, so that no disk is ever erased unasked"),
        ("InstallTo",
            "the answer file must not choose the target disk. The user picks it in Setup, so that Windows never lands on the wrong disk"),
        ("InstallToAvailablePartition",
            "the answer file must not choose the target partition. The user picks it in Setup, so that Windows never lands on the wrong partition"),
        ("AutoLogon",
            "the answer file must not sign anyone in automatically. It would store the password in the registry")
    ];

    /// <summary>
    ///     Builds the answer file. The result has passed <see cref="Validate" />.
    /// </summary>
    /// <param name="input">Image and Setup choices.</param>
    /// <param name="password">
    ///     Password for the named local account, asked for by the host at build time. Null or empty: no
    ///     password. It is never read from a profile, and this method keeps no copy of it.
    /// </param>
    /// <exception cref="PlainstallException">InvalidInput when a name or the password cannot be used.</exception>
    public static XDocument Generate(UnattendInput input, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        CheckInput(input, password);

        var setup = input.Setup;
        var root = new XElement(Ns + "unattend");
        if (input.VmTest)
        {
            // The usual first comment promises that no disk is touched. Here that would be a lie.
            root.Add(new XComment(" Created by Plainstall. This file answers the questions of Windows Setup. "));
            root.Add(new XComment(
                " WARNING: VIRTUAL MACHINE TEST MEDIA. This file erases disk 0 of whatever PC boots from this media, without asking, and installs Windows on it. It is made for an automated test in a virtual machine. Never boot a real PC from it. "));
        }
        else
        {
            root.Add(new XComment(
                " Created by Plainstall. This file answers some of the questions of Windows Setup. It never selects or erases a disk. "));
        }

        // windowsPE: what Setup needs before it copies Windows.
        var windowsPe = new XElement(Ns + "settings", new XAttribute("pass", PassWindowsPe));
        if (setup.HasLocaleSettings)
            windowsPe.Add(Component("Microsoft-Windows-International-Core-WinPE",
                E("SetupUILanguage", E("UILanguage", setup.UiLanguage)),
                Optional("InputLocale", setup.InputLocale),
                E("SystemLocale", setup.SystemLocale),
                E("UILanguage", setup.UiLanguage),
                Optional("UserLocale", setup.UserLocale)));
        windowsPe.Add(Component("Microsoft-Windows-Setup",
            input.VmTest ? VmTestDiskConfiguration() : null,
            E("ImageInstall",
                E("OSImage",
                    E("InstallFrom",
                        new XElement(Ns + "MetaData", new XAttribute(Wcm + "action", "add"),
                            E("Key", "/IMAGE/INDEX"),
                            E("Value", input.ImageIndex.ToString(CultureInfo.InvariantCulture)))),
                    input.VmTest
                        ? E("InstallTo",
                            E("DiskID", VmTestDiskId),
                            E("PartitionID", VmTestWindowsPartitionId))
                        : null)),
            E("UserData",
                E("AcceptEula", "true"))));
        root.Add(windowsPe);

        // specialize: machine-wide settings, before any account exists.
        if (input.ComputerName is not null || setup.TimeZone is not null)
            root.Add(new XElement(Ns + "settings", new XAttribute("pass", PassSpecialize),
                Component("Microsoft-Windows-Shell-Setup",
                    Optional("ComputerName", input.ComputerName),
                    Optional("TimeZone", setup.TimeZone))));

        // oobeSystem: the pages after the installation, the account and the first sign-in.
        var oobeSystem = new XElement(Ns + "settings", new XAttribute("pass", PassOobeSystem));
        if (setup.HasLocaleSettings)
            oobeSystem.Add(Component("Microsoft-Windows-International-Core",
                Optional("InputLocale", setup.InputLocale),
                E("SystemLocale", setup.SystemLocale),
                E("UILanguage", setup.UiLanguage),
                Optional("UserLocale", setup.UserLocale)));

        var localAccount = input.LocalAccount is not null;
        var shell = Component("Microsoft-Windows-Shell-Setup",
            E("OOBE",
                // The license was accepted in the windowsPE pass. Asking a second time helps nobody.
                E("HideEULAPage", "true"),
                localAccount ? E("HideOnlineAccountScreens", "true") : null,
                // The Wi-Fi page is what leads into the Microsoft account pages, so it goes with them.
                localAccount ? E("HideWirelessSetupInOOBE", "true") : null,
                input.SkipPrivacyPages ? E("ProtectYourPC", "3") : null));

        if (input.LocalAccount?.Name is { } accountName)
        {
            shell.Add(E("UserAccounts",
                E("LocalAccounts",
                    new XElement(Ns + "LocalAccount", new XAttribute(Wcm + "action", "add"),
                        E("Name", accountName),
                        E("DisplayName", accountName),
                        E("Group", "Administrators"),
                        PasswordElement(password)))));

            if (input.VmTest)
                // Test only: sign the new account in once, so that the first sign-in work starts
                // without anybody typing. Element names as in Microsoft's reference for
                // Microsoft-Windows-Shell-Setup/AutoLogon: Enabled, LogonCount (required whenever
                // AutoLogon is used), Username, and Password in the same form as for the account.
                shell.Add(E("AutoLogon",
                    E("Enabled", "true"),
                    E("LogonCount", "1"),
                    E("Username", accountName),
                    PasswordElement(password)));
        }

        if (input.HasMachineFirstLogonScript)
            shell.Add(E("FirstLogonCommands",
                new XElement(Ns + "SynchronousCommand", new XAttribute(Wcm + "action", "add"),
                    E("Order", "1"),
                    E("CommandLine", FirstLogonScriptGenerator.MachineCommandLine),
                    E("Description", "Plainstall: finish setting up this PC"))));
        oobeSystem.Add(shell);
        root.Add(oobeSystem);

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

        var problems = Validate(document, input.VmTest);
        if (problems.Count > 0)
            throw PlainstallException.WithList(ErrorKind.Internal,
                "The generated answer file is not valid. This is a bug in Plainstall:", problems);
        return document;
    }

    /// <summary>
    ///     The file text: XML declaration naming UTF-8, two-space indentation, CRLF line ends, a final
    ///     line end. The same document always gives the same text, which is what the expected-file tests
    ///     compare. <b>Contains the encoded password.</b> For logs use <see cref="ToLogSafeString" />.
    /// </summary>
    public static string ToXmlString(XDocument document)
    {
        return Encoding.UTF8.GetString(ToXmlBytes(document));
    }

    /// <summary>The bytes to write to the stick: <see cref="ToXmlString" /> as UTF-8 without a byte order mark.</summary>
    public static byte[] ToXmlBytes(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = false
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            document.Save(writer);
        }

        stream.Write("\r\n"u8);
        return stream.ToArray();
    }

    /// <summary>
    ///     The file text with every password value replaced by <see cref="PasswordMask" />. The document
    ///     passed in is not changed. This is the only form of the answer file that may reach a log.
    /// </summary>
    public static string ToLogSafeString(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var copy = new XDocument(document);
        foreach (var value in copy.Descendants()
                     .Where(e => e.Name.LocalName.EndsWith("Password", StringComparison.OrdinalIgnoreCase))
                     .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "Value")))
            value.Value = PasswordMask;
        return ToXmlString(copy);
    }

    /// <summary>
    ///     Checks the rules every Plainstall answer file must keep, whatever produced it. Used by
    ///     <see cref="Generate" /> on its own result and by the tests. Returns the problems found. An empty
    ///     list means the document is acceptable.
    /// </summary>
    /// <param name="document">The answer file.</param>
    /// <param name="allowVmTest">
    ///     False for every file that may reach a real PC: disk configuration, a fixed install target and
    ///     automatic sign-in are then reported as problems. True only for the virtual machine test
    ///     (<see cref="UnattendInput.VmTest" />), where exactly these are wanted. A <c>ProductKey</c>
    ///     element is a problem in both cases.
    /// </param>
    public static IReadOnlyList<string> Validate(XDocument document, bool allowVmTest = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        var problems = new List<string>();
        var root = document.Root;
        if (root is null || root.Name != Ns + "unattend")
        {
            problems.Add($"The root element must be <unattend> in the namespace {Ns.NamespaceName}.");
            return problems;
        }

        problems.AddRange(root.Descendants()
            .Where(e => e.Name.Namespace != Ns)
            .Select(element =>
                $"<{element.Name.LocalName}> is not in the namespace {Ns.NamespaceName}. Windows Setup would ignore it."));

        var seenPasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in root.Elements())
        {
            if (child.Name != Ns + "settings")
            {
                problems.Add($"<{child.Name.LocalName}> is not allowed directly under <unattend>. Only <settings> is.");
                continue;
            }

            var pass = (string?)child.Attribute("pass");
            if (pass is null || !ValidPasses.Contains(pass, StringComparer.Ordinal))
                problems.Add($"<settings pass=\"{pass}\"> does not name a configuration pass of Windows Setup.");
            else if (!seenPasses.Add(pass)) problems.Add($"The pass {pass} appears more than once.");

            var seenComponents = new HashSet<string>(StringComparer.Ordinal);
            foreach (var inner in child.Elements())
                if (inner.Name != Ns + "component")
                    problems.Add(
                        $"<{inner.Name.LocalName}> is not allowed directly under <settings pass=\"{pass}\">. Only <component> is.");
                else if ((string?)inner.Attribute("name") is { Length: > 0 } componentName &&
                         !seenComponents.Add(componentName))
                    problems.Add($"The component {componentName} appears more than once in the pass {pass}.");
        }

        foreach (var component in root.Descendants(Ns + "component"))
        {
            var name = (string?)component.Attribute("name");
            var label = string.IsNullOrEmpty(name) ? "A component" : name;
            if (component.Parent is not { } parent || parent.Name != Ns + "settings" || parent.Parent != root)
                problems.Add($"{label} is not directly inside a <settings pass=\"...\"> element.");
            if (string.IsNullOrEmpty(name)) problems.Add("A component has no name attribute.");

            var arch = (string?)component.Attribute("processorArchitecture");
            if (arch is null)
                problems.Add($"{label} has no processorArchitecture attribute.");
            else if (arch != ProcessorArchitecture)
                problems.Add($"{label} is for {arch}, but the image is {ProcessorArchitecture}.");
            RequireAttribute(component, label, "publicKeyToken", PublicKeyToken, problems);
            RequireAttribute(component, label, "language", "neutral", problems);
            RequireAttribute(component, label, "versionScope", "nonSxS", problems);
        }

        var forbiddenElements =
            allowVmTest ? AlwaysForbiddenElements : [.. AlwaysForbiddenElements, .. VmTestOnlyElements];
        foreach (var (forbidden, why) in forbiddenElements)
            // Compared by local name on purpose: a forbidden element in the wrong namespace is still a sign of a bug.
            if (root.Descendants().Any(e => e.Name.LocalName == forbidden))
                problems.Add($"<{forbidden}> found: {why}.");

        return problems;
    }

    /// <summary>
    ///     Test only: erase disk 0 and create the UEFI/GPT layout, without showing the disk page unless
    ///     something goes wrong.
    ///     <para>
    ///         The structure follows the UEFI example in Microsoft's answer-file reference for
    ///         Microsoft-Windows-Setup/DiskConfiguration. Element names relied on:
    ///         <c>DiskConfiguration/WillShowUI</c> (OnError);
    ///         <c>Disk</c> with <c>wcm:action="add"</c>, <c>DiskID</c> and <c>WillWipeDisk</c>;
    ///         <c>CreatePartitions/CreatePartition</c> with <c>Order</c>, <c>Type</c> (EFI, MSR, Primary),
    ///         <c>Size</c> in MB or <c>Extend</c>;
    ///         <c>ModifyPartitions/ModifyPartition</c> with <c>Order</c>, <c>PartitionID</c>, <c>Label</c>,
    ///         <c>Letter</c> and <c>Format</c>;
    ///         and, in <c>ImageInstall/OSImage</c>, <c>InstallTo</c> with <c>DiskID</c> and <c>PartitionID</c>.
    ///         As in that example, the Microsoft reserved partition is created but not modified: it has no
    ///         file system and no label.
    ///     </para>
    ///     <para>
    ///         There is no separate recovery partition. Setup then keeps the recovery environment on the
    ///         Windows partition, which is enough for a test machine that is thrown away afterwards.
    ///         A GPT layout needs UEFI firmware, so the virtual machine must be generation 2.
    ///     </para>
    /// </summary>
    private static XElement VmTestDiskConfiguration()
    {
        return E("DiskConfiguration",
            new XElement(Ns + "Disk", new XAttribute(Wcm + "action", "add"),
                E("DiskID", VmTestDiskId),
                E("WillWipeDisk", "true"),
                E("CreatePartitions",
                    new XElement(Ns + "CreatePartition", new XAttribute(Wcm + "action", "add"),
                        E("Order", "1"),
                        E("Type", "EFI"),
                        E("Size", VmTestEfiSizeMb)),
                    new XElement(Ns + "CreatePartition", new XAttribute(Wcm + "action", "add"),
                        E("Order", "2"),
                        E("Type", "MSR"),
                        E("Size", VmTestMsrSizeMb)),
                    new XElement(Ns + "CreatePartition", new XAttribute(Wcm + "action", "add"),
                        E("Order", "3"),
                        E("Type", "Primary"),
                        E("Extend", "true"))),
                E("ModifyPartitions",
                    new XElement(Ns + "ModifyPartition", new XAttribute(Wcm + "action", "add"),
                        E("Order", "1"),
                        E("PartitionID", VmTestEfiPartitionId),
                        E("Label", "System"),
                        E("Format", "FAT32")),
                    new XElement(Ns + "ModifyPartition", new XAttribute(Wcm + "action", "add"),
                        E("Order", "2"),
                        E("PartitionID", VmTestWindowsPartitionId),
                        E("Label", "Windows"),
                        E("Letter", "C"),
                        E("Format", "NTFS")))),
            E("WillShowUI", "OnError"));
    }

    /// <summary>
    ///     The hidden form of a password as Windows System Image Manager writes it for a local account:
    ///     Base64 of UTF-16LE(password + "Password"). Microsoft's own example in the LocalAccount
    ///     documentation decodes to exactly this shape.
    /// </summary>
    internal static string EncodePassword(string password)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(password + "Password"));
    }

    private static XElement PasswordElement(string? password)
    {
        return string.IsNullOrEmpty(password)
            ? E("Password", E("Value", ""), E("PlainText", "true"))
            : E("Password", E("Value", EncodePassword(password)), E("PlainText", "false"));
    }

    private static void CheckInput(UnattendInput input, string? password)
    {
        if (input.ImageIndex < 1)
            throw new PlainstallException(ErrorKind.Internal,
                $"The image index must be 1 or higher, not {input.ImageIndex}.");

        var setup = input.Setup ??
                    throw new PlainstallException(ErrorKind.Internal, "The resolved Setup options are missing.");
        if (!SetupResolver.IsLanguageTag(setup.UiLanguage) || !SetupResolver.IsLanguageTag(setup.SystemLocale))
            throw new PlainstallException(ErrorKind.InvalidInput,
                "The display language of the image is not a language tag such as en-US.");
        if (setup.UserLocale is not null && !SetupResolver.IsLanguageTag(setup.UserLocale))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The region '{setup.UserLocale}' is not a tag such as de-CH.");
        if (setup.InputLocale is not null && !SetupResolver.IsInputLocale(setup.InputLocale))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The keyboard '{setup.InputLocale}' is neither a tag such as de-CH nor a value such as 0409:00000409.");
        if (setup.TimeZone is not null && !SetupResolver.IsTimeZoneId(setup.TimeZone))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The time zone '{setup.TimeZone}' is not a Windows time zone id.");

        if (input.ComputerName is { } computerName && !ComputerNamePattern().IsMatch(computerName))
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The computer name '{computerName}' cannot be used. Use 1 to 15 letters, digits or hyphens, and not only digits.");

        var accountName = input.LocalAccount?.Name;
        if (input.VmTest && accountName is null)
            // Without a named account Setup stops at its account page and waits for somebody to type.
            throw new PlainstallException(ErrorKind.InvalidInput,
                "The virtual machine test needs a named local account, so that Windows Setup can create it and sign it in without anybody at the keyboard.");
        if (accountName is not null)
        {
            if (!AccountNamePattern().IsMatch(accountName)
                || accountName.Length > 20
                || accountName != accountName.Trim()
                || accountName.EndsWith('.')
                || accountName.Any(char.IsControl))
                throw new PlainstallException(ErrorKind.InvalidInput,
                    $"The account name '{accountName}' cannot be used. Use 1 to 20 characters, without \" / \\ [ ] : ; | = , + * ? < > @, and not ending in a dot or a space.");
            if (ProfileSerializer.IsReservedAccountName(accountName))
                throw new PlainstallException(ErrorKind.InvalidInput,
                    $"The account name '{accountName}' is already used by Windows itself. Choose another name.");
            if (string.Equals(accountName, input.ComputerName, StringComparison.OrdinalIgnoreCase))
                throw new PlainstallException(ErrorKind.InvalidInput,
                    "The account name and the computer name must be different. Windows does not accept the same name for both.");
        }

        var hasPassword = !string.IsNullOrEmpty(password);
        if (hasPassword && accountName is null)
            // Refused rather than dropped: the host believes it protected an account that will not exist.
            throw new PlainstallException(ErrorKind.InvalidInput,
                "A password was given, but the profile names no local account to give it to.");
        if (input.LocalAccount is { AskPassword: true } && accountName is not null && password is null)
            // Refused rather than creating an open account: the user chose to have a password.
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The profile asks for a password for the account '{accountName}', but none was given.");
        if (hasPassword && password!.Length > MaxPasswordLength)
            throw new PlainstallException(ErrorKind.InvalidInput,
                $"The password is too long. Windows accepts at most {MaxPasswordLength} characters.");
    }

    private static void RequireAttribute(XElement component, string label, string attribute, string expected,
        List<string> problems)
    {
        var actual = (string?)component.Attribute(attribute);
        if (actual != expected)
            problems.Add(actual is null
                ? $"{label} has no {attribute} attribute."
                : $"{label} has {attribute}=\"{actual}\". It must be \"{expected}\".");
    }

    /// <summary>
    ///     A component element in the form Windows System Image Manager writes: the four identity
    ///     attributes, and the two namespace prefixes declared on the component itself.
    /// </summary>
    private static XElement Component(string name, params object?[] content)
    {
        return new XElement(Ns + "component",
            new XAttribute("name", name),
            new XAttribute("processorArchitecture", ProcessorArchitecture),
            new XAttribute("publicKeyToken", PublicKeyToken),
            new XAttribute("language", "neutral"),
            new XAttribute("versionScope", "nonSxS"),
            new XAttribute(XNamespace.Xmlns + "wcm", Wcm.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
            content);
    }

    private static XElement E(string name, params object?[] content)
    {
        return new XElement(Ns + name, content);
    }

    private static XElement? Optional(string name, string? value)
    {
        return value is null ? null : E(name, value);
    }

    // The same patterns as the profile schema (Profiles\profile.schema.json), with \A and \z so that a trailing line break does not slip through.
    [GeneratedRegex(@"\A(?![0-9]+\z)[A-Za-z0-9-]{1,15}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ComputerNamePattern();

    [GeneratedRegex(@"\A[^""/\\\[\]:;|=,+*?<>@]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountNamePattern();
}