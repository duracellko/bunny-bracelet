using System.Diagnostics.CodeAnalysis;

using ByteArray = byte[];

namespace BunnyBracelet;

/// <summary>
/// Configuration of relay message authentication.
/// </summary>
public class RelayAuthenticationOptions
{
    /// <summary>
    /// Gets or sets the authentication key with index 1 for HMACSHA256 function to authenticate each message.
    /// </summary>
    /// <remarks>
    /// Either Key1 or Key2 can be used for authentication.
    /// Inbound Bunny Bracelet uses the one based on HTTP header.
    /// </remarks>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Authentication key is byte array.")]
    public ByteArray? Key1 { get; set; }

    /// <summary>
    /// Gets or sets the authentication key with index 2 for HMACSHA256 function to authenticate each message.
    /// </summary>
    /// <remarks>
    /// Either Key1 or Key2 can be used for authentication.
    /// Inbound Bunny Bracelet uses the one based on HTTP header.
    /// </remarks>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Authentication key is byte array.")]
    public ByteArray? Key2 { get; set; }

    /// <summary>
    /// Gets or sets index of the key that should be used by the outbound Bunny Bracelet.
    /// </summary>
    public int UseKeyIndex { get; set; }
}
