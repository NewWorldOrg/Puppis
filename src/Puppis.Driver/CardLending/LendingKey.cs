using System.Security.Cryptography;
using System.Text;

namespace Puppis.Driver.CardLending;

public sealed class LendingKey
{
    public const int ShortestText = 32;

    public const int LongestText = 4096;

    private readonly byte[] material;

    private LendingKey(byte[] material) => this.material = material;

    public static LendingKey FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string trimmed = text.Trim();
        if (trimmed.Length is < ShortestText or > LongestText)
        {
            throw new ArgumentException(
                $"A card-lending key is {ShortestText} to {LongestText} characters, not {trimmed.Length}.",
                nameof(text)
            );
        }

        return new LendingKey(Encoding.UTF8.GetBytes(trimmed));
    }

    public static LendingKey? ReadFile(string? path, out string? problem)
    {
        problem = null;

        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
        {
            problem = $"expected an absolute path to the key, got '{path}'.";

            return null;
        }

        try
        {
            if ((File.GetUnixFileMode(path) & OthersMayTouch) is not UnixFileMode.None)
            {
                problem = $"others may read or write '{path}'. Allow only its owner and group, as with 0600 or 0640.";

                return null;
            }

            return FromText(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            problem = $"'{path}' could not be read: {error.Message}";
        }
        catch (ArgumentException error)
        {
            problem = $"'{path}' does not hold a usable key: {error.Message}";
        }

        return null;
    }

    private const UnixFileMode OthersMayTouch = UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    internal byte[] Derive(
        string purpose,
        string host,
        ReadOnlySpan<byte> lenderNonce,
        ReadOnlySpan<byte> borrowerNonce
    )
    {
        byte[] message =
        [
            .. Encoding.ASCII.GetBytes(purpose),
            0,
            .. Encoding.UTF8.GetBytes(host.ToLowerInvariant()),
            0,
            .. lenderNonce,
            .. borrowerNonce,
        ];

        return HMACSHA256.HashData(material, message);
    }

    public override string ToString() => "a card-lending key";
}
