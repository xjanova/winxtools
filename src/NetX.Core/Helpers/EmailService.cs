using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace NetX.Core.Helpers;

/// <summary>
/// Email service with support for SMTP, OAuth, and various authentication methods
/// </summary>
public class EmailService : IDisposable
{
    private static EmailService? _instance;
    public static EmailService Instance => _instance ??= new EmailService();

    private readonly string _settingsPath;
    private EmailSettings _settings = new();
    private SmtpClient? _smtpClient;

    public event Action<string>? OnEmailSent;
    public event Action<string, Exception>? OnEmailFailed;

    public EmailService()
    {
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetX", "email_settings.json");

        LoadSettings();
    }

    #region Configuration

    public EmailSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            SaveSettings();
            ResetSmtpClient();
        }
    }

    public void Configure(string smtpServer, int port, string username, string password,
        bool useSsl = true, string? fromEmail = null, string? fromName = null)
    {
        _settings.SmtpServer = smtpServer;
        _settings.SmtpPort = port;
        _settings.Username = username;
        _settings.Password = EncryptPassword(password);
        _settings.UseSsl = useSsl;
        _settings.FromEmail = fromEmail ?? username;
        _settings.FromName = fromName ?? "WinXTools";
        _settings.IsConfigured = true;

        SaveSettings();
        ResetSmtpClient();
    }

    public void ConfigureGmail(string email, string appPassword)
    {
        Configure("smtp.gmail.com", 587, email, appPassword, true, email, "WinXTools");
        _settings.Provider = EmailProvider.Gmail;
        SaveSettings();
    }

    public void ConfigureOutlook(string email, string password)
    {
        Configure("smtp.office365.com", 587, email, password, true, email, "WinXTools");
        _settings.Provider = EmailProvider.Outlook;
        SaveSettings();
    }

    public void ConfigureCustom(string smtpServer, int port, string username, string password,
        bool useSsl, string fromEmail, string fromName, EmailAuthMethod authMethod)
    {
        Configure(smtpServer, port, username, password, useSsl, fromEmail, fromName);
        _settings.Provider = EmailProvider.Custom;
        _settings.AuthMethod = authMethod;
        SaveSettings();
    }

    private void ResetSmtpClient()
    {
        _smtpClient?.Dispose();
        _smtpClient = null;
    }

    #endregion

    #region Sending Emails

    /// <summary>
    /// Sends an email asynchronously
    /// </summary>
    public async Task<bool> SendEmailAsync(string to, string subject, string body,
        bool isHtml = false, List<string>? attachments = null, CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
        {
            OnEmailFailed?.Invoke(to, new InvalidOperationException("Email is not configured"));
            return false;
        }

        try
        {
            var client = GetSmtpClient();
            var message = CreateMailMessage(to, subject, body, isHtml, attachments);

            await client.SendMailAsync(message, cancellationToken);

            OnEmailSent?.Invoke(to);
            return true;
        }
        catch (Exception ex)
        {
            OnEmailFailed?.Invoke(to, ex);
            return false;
        }
    }

    /// <summary>
    /// Sends an email synchronously
    /// </summary>
    public bool SendEmail(string to, string subject, string body,
        bool isHtml = false, List<string>? attachments = null)
    {
        return SendEmailAsync(to, subject, body, isHtml, attachments).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Sends an email to multiple recipients
    /// </summary>
    public async Task<Dictionary<string, bool>> SendBulkEmailAsync(List<string> recipients,
        string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, bool>();

        foreach (var recipient in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[recipient] = await SendEmailAsync(recipient, subject, body, isHtml, null, cancellationToken);
            await Task.Delay(100, cancellationToken); // Rate limiting
        }

        return results;
    }

    private SmtpClient GetSmtpClient()
    {
        if (_smtpClient != null)
            return _smtpClient;

        _smtpClient = new SmtpClient(_settings.SmtpServer, _settings.SmtpPort)
        {
            EnableSsl = _settings.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(_settings.Username, DecryptPassword(_settings.Password)),
            Timeout = 30000
        };

        // Handle certificate validation for self-signed certs
        if (_settings.AllowInvalidCertificates)
        {
            ServicePointManager.ServerCertificateValidationCallback =
                (sender, certificate, chain, sslPolicyErrors) => true;
        }

        return _smtpClient;
    }

    private MailMessage CreateMailMessage(string to, string subject, string body,
        bool isHtml, List<string>? attachments)
    {
        var from = new MailAddress(_settings.FromEmail, _settings.FromName);
        var message = new MailMessage
        {
            From = from,
            Subject = subject,
            Body = body,
            IsBodyHtml = isHtml,
            Priority = MailPriority.Normal,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };

        // Handle multiple recipients (comma-separated)
        foreach (var recipient in to.Split(',', ';').Select(r => r.Trim()))
        {
            if (!string.IsNullOrEmpty(recipient))
                message.To.Add(recipient);
        }

        // Add attachments
        if (attachments != null)
        {
            foreach (var path in attachments)
            {
                if (File.Exists(path))
                {
                    message.Attachments.Add(new Attachment(path));
                }
            }
        }

        return message;
    }

    #endregion

    #region Template Support

    /// <summary>
    /// Sends an email using a template with variable replacement
    /// </summary>
    public async Task<bool> SendTemplateEmailAsync(string to, string subject,
        string templateBody, Dictionary<string, string> variables, CancellationToken cancellationToken = default)
    {
        var body = templateBody;

        foreach (var variable in variables)
        {
            body = body.Replace($"{{{variable.Key}}}", variable.Value);
            body = body.Replace($"{{{{variable.Key}}}}", variable.Value);
        }

        // Default variables
        body = body.Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"));
        body = body.Replace("{time}", DateTime.Now.ToString("HH:mm:ss"));
        body = body.Replace("{datetime}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        return await SendEmailAsync(to, subject, body, body.Contains("<html"), null, cancellationToken);
    }

    #endregion

    #region Testing

    /// <summary>
    /// Tests the email configuration by sending a test email
    /// </summary>
    public async Task<EmailTestResult> TestConnectionAsync(string? testRecipient = null)
    {
        var result = new EmailTestResult();

        if (!_settings.IsConfigured)
        {
            result.Success = false;
            result.ErrorMessage = "Email is not configured";
            return result;
        }

        try
        {
            var client = GetSmtpClient();

            // Test SMTP connection
            using var tcpClient = new System.Net.Sockets.TcpClient();
            await tcpClient.ConnectAsync(_settings.SmtpServer, _settings.SmtpPort);
            result.CanConnect = true;

            // If test recipient provided, send a test email
            if (!string.IsNullOrEmpty(testRecipient))
            {
                var success = await SendEmailAsync(testRecipient,
                    "WinXTools - Email Test",
                    "This is a test email from WinXTools to verify your email configuration is working correctly.\n\n" +
                    $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    "If you received this email, your configuration is correct.");

                result.TestEmailSent = success;
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    #endregion

    #region Persistence

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<EmailSettings>(json);
                if (settings != null)
                    _settings = settings;
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_settings,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    #endregion

    #region Encryption

    private static readonly byte[] _entropy = { 78, 101, 116, 88, 69, 109, 97, 105, 108 }; // "NetXEmail"

    private static string EncryptPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            return "";

        try
        {
            var data = Encoding.UTF8.GetBytes(password);
            var encrypted = ProtectedData.Protect(data, _entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch
        {
            // If encryption fails, store base64 encoded (less secure fallback)
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        }
    }

    private static string DecryptPassword(string encryptedPassword)
    {
        if (string.IsNullOrEmpty(encryptedPassword))
            return "";

        try
        {
            var data = Convert.FromBase64String(encryptedPassword);
            var decrypted = ProtectedData.Unprotect(data, _entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            // Try fallback (base64 only)
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encryptedPassword));
            }
            catch
            {
                return encryptedPassword;
            }
        }
    }

    #endregion

    public void Dispose()
    {
        _smtpClient?.Dispose();
    }
}

#region Settings & Models

public class EmailSettings
{
    public bool IsConfigured { get; set; }
    public EmailProvider Provider { get; set; } = EmailProvider.Custom;
    public string SmtpServer { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "WinXTools";
    public EmailAuthMethod AuthMethod { get; set; } = EmailAuthMethod.Basic;
    public bool AllowInvalidCertificates { get; set; } = false;

    // Default recipients for notifications
    public List<string> DefaultRecipients { get; set; } = new();
}

public enum EmailProvider
{
    Custom,
    Gmail,
    Outlook,
    Yahoo,
    SendGrid,
    Mailgun
}

public enum EmailAuthMethod
{
    Basic,
    OAuth2,
    PlainText,
    Login,
    CramMd5
}

public class EmailTestResult
{
    public bool Success { get; set; }
    public bool CanConnect { get; set; }
    public bool TestEmailSent { get; set; }
    public string? ErrorMessage { get; set; }
}

#endregion
