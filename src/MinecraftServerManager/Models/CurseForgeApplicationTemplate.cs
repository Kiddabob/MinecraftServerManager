namespace MinecraftServerManager.Models;

public sealed record CurseForgeApplicationTemplateField(
    string FormArea,
    string SuggestedAnswer);

public static class CurseForgeApplicationTemplate
{
    public const string ProjectName = "Minecraft Server Manager";
    public const string ProjectUrl = "https://github.com/Kiddabob/MinecraftServerManager";
    public const string TermsUrl = "https://support.curseforge.com/support/solutions/articles/9000207405-curse-forge-3rd-party-api-terms-and-conditions";
    public const string ApplicationGuideUrl = "https://support.curseforge.com/support/solutions/articles/9000208346-about-the-curseforge-api-and-how-to-apply-for-a-key";
    public const string ApplicationFormUrl = "https://forms.monday.com/forms/dce5ccb7afda9a1c21dab1a1aa1d84eb?r=use1";

    public static IReadOnlyList<string> PersonalFields { get; } =
    [
        "Your real name",
        "Your contact email",
        "Your full Discord username",
        "Your creator, gaming, or developer nickname"
    ];

    public static IReadOnlyList<CurseForgeApplicationTemplateField> SuggestedAnswers { get; } =
    [
        new(
            "Project name",
            ProjectName),
        new(
            "Project or source URL",
            ProjectUrl),
        new(
            "Monetization and business model",
            "No monetization and no business model. Minecraft Server Manager is a free Windows desktop application with no advertising, subscriptions, paid tiers, affiliate payments, or sale of user data. If a project page exposes an author's own donation, sponsorship, or paid-content link, the app will preserve that author's official link and will not take a share."),
        new(
            "What the project does",
            "Minecraft Server Manager is a local Windows desktop application for managing Minecraft Java servers and helping a user plan compatible client and server packs. Its primary functionality works without CurseForge and includes server-profile import, Java and memory selection, server launch, live console and safe stop controls, configuration editing, resource monitoring, player playtime, and verified local imports. The optional provider-neutral pack builder searches supported catalogues, lets the user choose an exact Minecraft version and loader, resolves declared dependencies and client/server placement, and creates a reviewed local content bundle."),
        new(
            "Why CurseForge API access is requested",
            "CurseForge contains projects and compatible file versions that are not always available from other supported catalogues. Optional API access would let this local installation show those projects alongside other sources, identify exact Minecraft and loader compatibility, resolve declared dependencies, and perform a user-requested download only through a route CurseForge permits. The app is not intended to replace CurseForge accounts, uploads, author tools, premium features, or a user's CurseForge library. A future client-pack workflow is intended to hand the client result to the CurseForge launcher rather than replace its authenticated launch experience."),
        new(
            "Author earnings, consent, and distribution",
            "CurseForge results will retain visible source and author attribution. The app will not scrape CurseForge, re-host files, proxy downloads, bypass a project's distribution choice, or manufacture a download URL. A CurseForge file will be offered only when the official API provides a supported download route and the user deliberately selects it. Official author support links can remain available without interception. If Overwolf requires a different launcher hand-off or another author-protection mechanism, the CurseForge path will remain disabled until that requirement is implemented."),
        new(
            "Expected API use, traffic, and retained information",
            "Expected use is low-volume and interactive for one person on one Windows PC. There is no background catalogue polling: searches and compatible-version requests occur only after a user action, and selected files are downloaded once for that user's local pack. Search results are not kept as a persistent catalogue cache. The app currently plans to retain a small local audit manifest after a download containing the selected provider, project and version identifiers, file names, sizes, and published hashes. Because the API terms restrict saving or caching API data, this application explicitly asks Overwolf to confirm whether that minimal per-user audit manifest is permitted; it will be constrained or removed for CurseForge content if required."),
        new(
            "Per-installation API key model and additional notes",
            "This application is for the applicant's own local installation of Minecraft Server Manager. It has no central API proxy, shared service, embedded application-wide key, or key redistribution. CurseForge access stays disabled until this user applies, Overwolf approves the request, and the user enters their own unique key. Requests then go directly from that user's PC. The key is stored only in Windows Credential Manager for that Windows account and is never written to a profile, pack, installer, update, log, manifest, GitHub repository, or telemetry. Please confirm that this bring-your-own-key local workflow is permitted before the applicant enables CurseForge access.")
    ];

    public static string CreatePlainText()
    {
        var personalFields = string.Join(
            Environment.NewLine,
            PersonalFields.Select(field => $"- {field}: [enter your own accurate details]"));
        var suggestedAnswers = string.Join(
            Environment.NewLine + Environment.NewLine,
            SuggestedAnswers.Select(field => $"{field.FormArea}:{Environment.NewLine}{field.SuggestedAnswer}"));

        return $"""
            CurseForge / Overwolf application preparation template

            PERSONAL DETAILS - enter these yourself; Minecraft Server Manager does not collect or save them.
            {personalFields}

            SUGGESTED PROJECT ANSWERS - review and change anything that is not true for your use.
            {suggestedAnswers}

            ACKNOWLEDGEMENTS
            Read the current official form, API terms, and privacy policy yourself. Only tick acknowledgements you personally understand and accept, including any statements about legal capacity, author distribution choices, API quotas or costs, and the accuracy of your submission. Minecraft Server Manager does not accept or submit them for you. Overwolf alone decides whether this proposed local workflow is approved.
            """;
    }
}
