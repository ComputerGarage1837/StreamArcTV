using System.Diagnostics;
using System.IO;
using System.Net.Mail;
using System.Text.Json;
using TheChocolateRabbit.Util;

namespace TheChocolateRabbit.Newsletter;

/// <summary>
/// Newsletter sign-ups. The newsletter service itself is not live yet, so each sign-up is
/// delivered to <see cref="BuildInfo.NewsletterEmail"/> (info@thechocolaterabbit.ca): the details
/// are handed to the user's email app as a ready-to-send message, and a copy is appended to
/// signups.jsonl under %LocalAppData%\TheChocolateRabbit so nothing is lost.
/// </summary>
public static class Signup
{
    public record Entry(string Name, string Email, DateTimeOffset At);

    private static string LocalFile => Path.Combine(AppPaths.Data, "signups.jsonl");

    /// Returns a message for the user when the details are not usable, otherwise null.
    public static string? Validate(string name, string email)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Please tell us your name.";
        if (name.Trim().Length > 120) return "That name is a little long — 120 characters at most.";
        if (string.IsNullOrWhiteSpace(email)) return "Please enter your email address.";
        if (!IsEmail(email.Trim())) return "That email address doesn't look right. Please check it.";
        return null;
    }

    public static bool IsEmail(string s)
    {
        if (s.Length > 254 || s.Contains(' ')) return false;
        var at = s.IndexOf('@');
        if (at <= 0 || at != s.LastIndexOf('@')) return false;
        var domain = s[(at + 1)..];
        if (!domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.')) return false;
        try { return new MailAddress(s).Address == s; } catch { return false; }
    }

    /// Appends the sign-up to the local record (one JSON object per line).
    public static void SaveLocal(Entry e)
    {
        try
        {
            AppPaths.Ensure();
            var line = JsonSerializer.Serialize(new { name = e.Name, email = e.Email, at = e.At.ToString("o"), app = BuildInfo.VersionName });
            File.AppendAllText(LocalFile, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            AppLog.E("Signup", "couldn't save the local copy", ex);
        }
    }

    public static string Subject(Entry e) => $"Newsletter sign-up: {e.Name}";

    public static string Body(Entry e) =>
        "New newsletter sign-up from The Chocolate Rabbit app\r\n" +
        "\r\n" +
        $"Name:  {e.Name}\r\n" +
        $"Email: {e.Email}\r\n" +
        $"Date:  {e.At.LocalDateTime:yyyy-MM-dd HH:mm}\r\n" +
        "\r\n" +
        $"Sent from The Chocolate Rabbit for Windows v{BuildInfo.VersionName}.\r\n";

    /// The mailto: link that opens the user's email app with the message ready to send.
    public static string MailtoUrl(Entry e) =>
        $"mailto:{BuildInfo.NewsletterEmail}?subject={Uri.EscapeDataString(Subject(e))}&body={Uri.EscapeDataString(Body(e))}";

    /// Opens the user's email app with the sign-up ready to send. Returns false when Windows has
    /// no email app to hand it to.
    public static bool OpenMail(Entry e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(MailtoUrl(e)) { UseShellExecute = true });
            AppLog.I("Signup", "handed a sign-up to the email app");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.W("Signup", "no email app took the mailto link: " + ex.Message);
            return false;
        }
    }
}
