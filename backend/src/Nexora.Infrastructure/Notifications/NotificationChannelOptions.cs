namespace Nexora.Infrastructure.Notifications;

/// <summary>
/// Options SMTP pour le canal email (Phase 34 §3).
/// Configurées sous la section <c>Notifications:Smtp</c> dans appsettings ou les secrets.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Notifications:Smtp";

    /// <summary>Hôte du serveur SMTP (ex. smtp.sendgrid.net, smtp.mailgun.org).</summary>
    public string Host { get; set; } = "";

    /// <summary>Port SMTP — 587 (STARTTLS) ou 465 (SSL/TLS).</summary>
    public int Port { get; set; } = 587;

    /// <summary>Nom d'utilisateur SMTP (souvent "apikey" chez SendGrid).</summary>
    public string UserName { get; set; } = "";

    /// <summary>Mot de passe / clé d'API SMTP.</summary>
    public string Password { get; set; } = "";

    /// <summary>Adresse expéditeur — ex. "Nexora One &lt;no-reply@monentreprise.com&gt;".</summary>
    public string FromAddress { get; set; } = "";

    /// <summary>Utiliser SSL/TLS dès la connexion (port 465). False = STARTTLS (port 587).</summary>
    public bool UseSsl { get; set; } = false;

    /// <summary>Activer le canal. False = canal désactivé, repli sur le log (comportement dev).</summary>
    public bool Enabled { get; set; } = false;
}

/// <summary>
/// Options Twilio pour le canal SMS (Phase 34 §3).
/// Configurées sous la section <c>Notifications:Twilio</c>.
/// </summary>
public sealed class TwilioOptions
{
    public const string SectionName = "Notifications:Twilio";

    /// <summary>Account SID Twilio (commence par "AC…").</summary>
    public string AccountSid { get; set; } = "";

    /// <summary>Auth Token Twilio.</summary>
    public string AuthToken { get; set; } = "";

    /// <summary>Numéro expéditeur Twilio au format E.164 — ex. "+33100000000".</summary>
    public string FromNumber { get; set; } = "";

    /// <summary>Activer le canal. False = canal désactivé, repli sur le log.</summary>
    public bool Enabled { get; set; } = false;
}

/// <summary>
/// Options Firebase Cloud Messaging pour le canal push (Phase 34 §3, Phase 44 §20).
/// Configurées sous la section <c>Notifications:Fcm</c>.
/// </summary>
public sealed class FcmOptions
{
    public const string SectionName = "Notifications:Fcm";

    /// <summary>
    /// Chemin vers le fichier JSON de compte de service Firebase,
    /// ou la valeur JSON complète inline (pratique pour les secrets d'environnement).
    /// L'un ou l'autre doit être renseigné pour activer le canal.
    /// </summary>
    public string ServiceAccountJson { get; set; } = "";

    /// <summary>Activer le canal. False = canal désactivé, repli sur le log.</summary>
    public bool Enabled { get; set; } = false;
}
