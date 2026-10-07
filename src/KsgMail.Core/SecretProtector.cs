using System.Security.Cryptography;
using System.Text;

namespace KsgMail.Core;

public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string encrypted);
}

public sealed class WindowsSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KsgMail.Secrets.v1");

    public string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) throw new UserException("Hasło nie może być puste.");
        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public string Unprotect(string encrypted)
    {
        try
        {
            var plaintext = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new UserException("Nie można odczytać hasła. Zapisz je ponownie na tym koncie Windows.");
        }
    }
}
