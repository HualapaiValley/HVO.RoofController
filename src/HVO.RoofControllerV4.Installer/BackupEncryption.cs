using System.Security.Cryptography;
using System.Text;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer;

/// <summary>
/// Optional, versioned authenticated encryption around the existing gzip archive. The passphrase and derived key
/// live only in memory; no plaintext temporary archive is written. Version 1 fixes the KDF cost, so an untrusted
/// header cannot ask restore to do unbounded work. All header bytes are authenticated along with the ciphertext.
/// </summary>
internal sealed class BackupEncryption(byte[] passphrase) : IDisposable
{
    private static ReadOnlySpan<byte> Family => "HVO-ROOF-BACKUP-"u8;
    private static ReadOnlySpan<byte> Magic => "HVO-ROOF-BACKUP-AES256-GCM-v1\n"u8;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int Iterations = 600_000;

    /// <summary>The longest passphrase, in UTF-8 bytes, from a file or the prompt alike.</summary>
    internal const int MaxPassphraseBytes = 4096;

    // What a passphrase file may hold beyond the passphrase: its final LF or CRLF, which is not part of it.
    private const int LineEndingBytes = 2;
    private static readonly string FileLimit = $"{MaxPassphraseBytes} bytes of passphrase (a final LF or CRLF is not counted)";
    private static int HeaderBytes => Magic.Length + SaltBytes + NonceBytes;

    internal static bool IsEncrypted(ReadOnlySpan<byte> archive) => archive.StartsWith(Family);

    internal static async Task<BackupEncryption?> LoadAsync(InstallerMachine machine, string? file, CancellationToken cancellationToken)
    {
        if (file is null) return null;
        var path = Path.GetFullPath(file, machine.CurrentDirectory);
        // A purge must not remove the only way to decrypt the archive. Nor should a backup collect its own password.
        BackupCommands.RefuseRemovedOutput(path, subject: "passphrase file");
        BackupCommands.RefuseRemovedOutput(Path.Join(machine.ResolveFolder(Path.GetDirectoryName(path)!), Path.GetFileName(path)), subject: "passphrase file");
        // The limit is on the passphrase, as at the prompt, so the file is read with room for its line ending: a
        // passphrase of exactly the limit followed by LF or CRLF is the same passphrase the prompt accepts.
        var bytes = await BackupCommands.ReadPrivateAsync(
            machine, path, cancellationToken, maxBytes: MaxPassphraseBytes + LineEndingBytes, subject: "passphrase file", limit: FileLimit).ConfigureAwait(false);
        try
        {
            var length = bytes.Length;
            if (length > 0 && bytes[length - 1] == '\n') length--;
            if (length > 0 && bytes[length - 1] == '\r') length--;
            if (length > MaxPassphraseBytes)
            {
                throw new InstallerRefusedException($"The passphrase file {path} is more than {FileLimit}. Nothing was changed.");
            }

            return FromBytes(bytes.AsSpan(0, length));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static BackupEncryption Prompt(InstallerHost host)
    {
        if (!host.IsInteractive)
            throw new InstallerUsageException("This is an encrypted backup: give --passphrase-file FILE when there is no terminal to read its passphrase. Nothing was restored.");
        var typed = host.ReadSecret("the backup's passphrase")
            ?? throw new InstallerException("Restore cancelled. Nothing was restored.", InstallerExitCode.Cancelled);
        byte[] bytes;
        try { bytes = new UTF8Encoding(false, true).GetBytes(typed); }
        catch (EncoderFallbackException)
        {
            throw new InstallerRefusedException("The passphrase must be one nonempty UTF-8 line, at most 4096 bytes. Nothing was restored.");
        }
        try { return FromBytes(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static BackupEncryption FromBytes(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || value.Length > MaxPassphraseBytes || value.ContainsAny((byte)'\n', (byte)'\r', (byte)0))
            throw new InstallerRefusedException("The passphrase must be one nonempty UTF-8 line, at most 4096 bytes. Nothing was changed.");
        try { _ = new UTF8Encoding(false, true).GetCharCount(value); }
        catch (DecoderFallbackException)
        {
            throw new InstallerRefusedException("The passphrase must be one nonempty UTF-8 line. Nothing was changed.");
        }
        return new BackupEncryption(value.ToArray());
    }

    internal byte[] Encrypt(ReadOnlySpan<byte> plaintext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var archive = new byte[checked(HeaderBytes + plaintext.Length + TagBytes)];
        Magic.CopyTo(archive);
        var salt = archive.AsSpan(Magic.Length, SaltBytes);
        var nonce = archive.AsSpan(Magic.Length + SaltBytes, NonceBytes);
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);
        Span<byte> key = stackalloc byte[32];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, key, Iterations, HashAlgorithmName.SHA256);
            cancellationToken.ThrowIfCancellationRequested();
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, archive.AsSpan(HeaderBytes, plaintext.Length), archive.AsSpan(archive.Length - TagBytes), archive.AsSpan(0, HeaderBytes));
            return archive;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal byte[] Decrypt(ReadOnlySpan<byte> archive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!archive.StartsWith(Magic) || archive.Length < HeaderBytes + TagBytes)
            throw new InstallerRefusedException("Cannot read encrypted backup: its header is incomplete or its encryption version is not supported. Nothing was restored.");
        var plaintext = new byte[archive.Length - HeaderBytes - TagBytes];
        Span<byte> key = stackalloc byte[32];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(passphrase, archive.Slice(Magic.Length, SaltBytes), key, Iterations, HashAlgorithmName.SHA256);
            cancellationToken.ThrowIfCancellationRequested();
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(archive.Slice(Magic.Length + SaltBytes, NonceBytes), archive.Slice(HeaderBytes, plaintext.Length), archive[^TagBytes..], plaintext, archive[..HeaderBytes]);
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InstallerRefusedException("Cannot read encrypted backup: the passphrase is wrong or the archive is damaged. Nothing was restored.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(passphrase);
}
