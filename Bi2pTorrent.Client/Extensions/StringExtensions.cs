using System;
using System.Collections.Generic;
using System.Text;

using Multiformats.Base;

namespace Bi2pTorrent.Client.Extensions;

public static class StringExtensions
{
    public static byte[] FromB32AddressToBytes(this string base32)
    {
        if (!base32.EndsWith(".b32.i2p", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid B32 address: {base32}");
        }
        
        base32 = base32[..^".b32.i2p".Length];
        var bytes = Multibase.DecodeRaw(MultibaseEncoding.Base32Lower, base32);

        return bytes;
    }
}
