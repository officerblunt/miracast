using System.Buffers.Binary;
using System.Text;

namespace Miracast.Receiver.Linux;

internal sealed class WfdDisplayCapabilities
{
    private const int FrameRate = 30;
    private readonly byte[]? _edid;
    private readonly int _decoderWidth;
    private readonly int _decoderHeight;

    public WfdDisplayCapabilities(
        int width,
        int height,
        int? nativeWidth = null,
        int? nativeHeight = null)
    {
        if (width is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(height));

        nativeWidth ??= width;
        nativeHeight ??= height;
        if (nativeWidth is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(nativeWidth));
        if (nativeHeight is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(nativeHeight));

        Width = width;
        Height = height;
        NativeWidth = nativeWidth.Value;
        NativeHeight = nativeHeight.Value;
        // The codec must also accept common source-native mirror modes. These
        // limits describe the decoder, while EDID describes the exact virtual
        // desktop exposed in Extend mode.
        _decoderWidth = Math.Max(Math.Max(width, NativeWidth), 4096);
        _decoderHeight = Math.Max(Math.Max(height, NativeHeight), 2160);
        _edid = SyntheticEdid.TryCreate(
            width,
            height,
            NativeWidth,
            NativeHeight,
            FrameRate);
    }

    public int Width { get; }
    public int Height { get; }
    public int NativeWidth { get; }
    public int NativeHeight { get; }
    public bool HasEdid => _edid is not null;

    public string LegacyVideoFormats =>
        $"{GetLegacyNativeMode():x2} 01 02 10 {BuildCeaMask(includeExtensions: false):x8} "
        + $"{BuildVesaMask(includeExtensions: false):x8} {BuildHandheldMask():x8} "
        + $"00 0000 0000 00 {_decoderWidth:x4} {_decoderHeight:x4}";

    public string ExtendedVideoFormats
    {
        get
        {
            return $"{GetExtendedNativeMode():x4} 01 0002 0080 "
                + $"{BuildCeaMask(includeExtensions: true):x10} "
                + $"{BuildVesaMask(includeExtensions: true):x10} {BuildHandheldMask():x8} "
                + $"00 0000 0000 00 {_decoderWidth:x4} {_decoderHeight:x4}";
        }
    }

    /// <summary>
    /// Wi-Fi Display R2 video capabilities. This is deliberately separate
    /// from Microsoft's wfdx_video_formats extension: Windows requests the
    /// standard WFD2 parameter and will fall back to WFD1 modes if it is
    /// answered with "none".
    /// </summary>
    public string Wfd2VideoFormats
    {
        get
        {
            var common = $"{BuildWfd2CeaMask():x12} {BuildWfd2VesaMask():x12} "
                + $"{BuildWfd2HandheldMask():x12} 00 0000 0000 00";
            return $"{GetWfd2NativeMode():x2} 01 01 0080 {common}, "
                + $"01 02 0080 {common} 00";
        }
    }

    public string MicrosoftCustomVideoFormats =>
        $"{Width:x4} {Height:x4} {FrameRate:x4}";

    // "none" rather than an all-zero mask, so Windows treats the fixed
    // Surface-shaped table as unsupported instead of an empty ceiling.
    public string MicrosoftVideoFormats
    {
        get
        {
            var mask = BuildMicrosoftVideoFormatMask();
            return mask == 0 ? "none" : $"{mask:x12}";
        }
    }

    public string DisplayEdid => _edid is null
        ? "none"
        : $"{_edid.Length / 128:x4} {Convert.ToHexString(_edid)}";

    public bool Contains(int width, int height) =>
        width > 0 && height > 0 && width <= Width && height <= Height;

    public bool CanDecode(int width, int height) =>
        width > 0 && height > 0 && width <= _decoderWidth && height <= _decoderHeight;

    private int GetLegacyNativeMode()
    {
        return SelectBestNativeMode(
            (640, 480, 0),
            (1280, 720, 5 << 3),
            (1366, 768, (12 << 3) | 1),
            (1920, 1080, 7 << 3));
    }

    private int GetExtendedNativeMode()
    {
        return SelectBestNativeMode(
            (640, 480, 0),
            (1280, 720, 5 << 3),
            (1366, 768, (12 << 3) | 1),
            (1920, 1080, 7 << 3),
            (1920, 1200, (28 << 3) | 1),
            (2560, 1440, (29 << 3) | 1),
            (2560, 1600, (31 << 3) | 1),
            (3840, 2160, 17 << 3),
            (4096, 2160, 19 << 3));
    }

    private int GetWfd2NativeMode()
    {
        // WFD2 native modes use a two-bit table selector followed by a
        // six-bit index (Table 74), unlike the three-bit selector in WFD1.
        return SelectBestNativeMode(
            (640, 480, 0),
            (1280, 720, 5 << 2),
            (1366, 768, (12 << 2) | 1),
            (1920, 1080, 7 << 2),
            (1920, 1200, (28 << 2) | 1),
            (2560, 1440, (30 << 2) | 1),
            (2560, 1600, (32 << 2) | 1),
            (3840, 2160, 19 << 2),
            (4096, 2160, 24 << 2));
    }

    private int SelectBestNativeMode(params (int Width, int Height, int Code)[] modes)
    {
        var bestCode = modes[0].Code;
        var bestPresentedArea = -1d;
        var bestScalePenalty = double.MaxValue;

        foreach (var mode in modes)
        {
            if (!CanDecode(mode.Width, mode.Height))
                continue;

            // Use the receiver window's "contain" calculation, but do not
            // reward a low-resolution mode merely because the UI can upscale
            // it. Prefer the mode that covers the largest native-size area
            // without cropping or changing its aspect ratio.
            var scale = Math.Min(1d, Math.Min(
                Width / (double)mode.Width,
                Height / (double)mode.Height));
            var presentedArea = mode.Width * scale * mode.Height * scale;
            var scalePenalty = 1d - scale;
            if (presentedArea > bestPresentedArea + 0.5
                || (Math.Abs(presentedArea - bestPresentedArea) <= 0.5
                    && scalePenalty < bestScalePenalty))
            {
                bestCode = mode.Code;
                bestPresentedArea = presentedArea;
                bestScalePenalty = scalePenalty;
            }
        }

        return bestCode;
    }

    private ulong BuildWfd2CeaMask()
    {
        ulong mask = 1; // 640x480p60
        Add(5, 1280, 720);  // p30
        Add(6, 1280, 720);  // p60
        Add(7, 1920, 1080); // p30
        Add(8, 1920, 1080); // p60
        Add(15, 1280, 720); // p24
        Add(16, 1920, 1080); // p24
        Add(17, 3840, 2160); // p24
        Add(18, 3840, 2160); // p25
        Add(19, 3840, 2160); // p30
        Add(22, 4096, 2160); // p24
        Add(23, 4096, 2160); // p25
        Add(24, 4096, 2160); // p30
        return mask;

        void Add(int bit, int width, int height)
        {
            if (_decoderWidth >= width && _decoderHeight >= height)
                mask |= 1UL << bit;
        }
    }

    private ulong BuildWfd2VesaMask()
    {
        ulong mask = 0;
        AddPair(0, 800, 600);
        AddPair(2, 1024, 768);
        AddPair(4, 1152, 864);
        AddPair(6, 1280, 768);
        AddPair(8, 1280, 800);
        AddPair(10, 1360, 768);
        AddPair(12, 1366, 768);
        AddPair(14, 1280, 1024);
        AddPair(16, 1400, 1050);
        AddPair(18, 1440, 900);
        AddPair(20, 1600, 900);
        AddPair(22, 1600, 1200);
        AddPair(24, 1680, 1024);
        AddPair(26, 1680, 1050);
        AddPair(28, 1920, 1200);
        AddPair(30, 2560, 1440);
        AddPair(32, 2560, 1600);
        return mask;

        void AddPair(int bit, int width, int height)
        {
            if (_decoderWidth < width || _decoderHeight < height)
                return;
            mask |= 1UL << bit; // p30
            mask |= 1UL << (bit + 1); // p60
        }
    }

    private ulong BuildWfd2HandheldMask()
    {
        ulong mask = 0;
        AddPair(0, 800, 480);
        AddPair(2, 854, 480);
        AddPair(4, 864, 480);
        AddPair(6, 640, 360);
        AddPair(8, 960, 540);
        AddPair(10, 848, 480);
        return mask;

        void AddPair(int bit, int width, int height)
        {
            if (_decoderWidth < width || _decoderHeight < height)
                return;
            mask |= 1UL << bit;
            mask |= 1UL << (bit + 1);
        }
    }

    private ulong BuildCeaMask(bool includeExtensions)
    {
        ulong mask = 1;
        if (_decoderWidth >= 720 && _decoderHeight >= 480)
            mask |= 1UL << 1;
        if (_decoderWidth >= 1280 && _decoderHeight >= 720)
            mask |= 1UL << 5;
        if (_decoderWidth >= 1920 && _decoderHeight >= 1080)
            mask |= 1UL << 7;
        if (includeExtensions && _decoderWidth >= 3840 && _decoderHeight >= 2160)
            mask |= 1UL << 17;
        if (includeExtensions && _decoderWidth >= 4096 && _decoderHeight >= 2160)
            mask |= 1UL << 19;
        return mask;
    }

    private ulong BuildVesaMask(bool includeExtensions)
    {
        ulong mask = 0;
        Add(0, 800, 600);
        Add(2, 1024, 768);
        Add(4, 1152, 864);
        Add(6, 1280, 768);
        Add(8, 1280, 800);
        Add(10, 1360, 768);
        Add(12, 1366, 768);
        Add(14, 1280, 1024);
        Add(16, 1400, 1050);
        Add(18, 1440, 900);
        Add(20, 1600, 900);
        Add(22, 1600, 1200);
        Add(24, 1680, 1024);
        Add(26, 1680, 1050);
        Add(28, 1920, 1200);
        if (includeExtensions)
        {
            Add(29, 2560, 1440);
            Add(31, 2560, 1600);
        }
        return mask;

        void Add(int bit, int width, int height)
        {
            if (_decoderWidth >= width && _decoderHeight >= height)
                mask |= 1UL << bit;
        }
    }

    private ulong BuildHandheldMask()
    {
        ulong mask = 0;
        if (_decoderWidth >= 800 && _decoderHeight >= 480)
            mask |= 1UL << 0;
        if (_decoderWidth >= 854 && _decoderHeight >= 480)
            mask |= 1UL << 2;
        if (_decoderWidth >= 864 && _decoderHeight >= 480)
            mask |= 1UL << 4;
        if (_decoderWidth >= 640 && _decoderHeight >= 360)
            mask |= 1UL << 6;
        if (_decoderWidth >= 960 && _decoderHeight >= 540)
            mask |= 1UL << 8;
        if (_decoderWidth >= 848 && _decoderHeight >= 480)
            mask |= 1UL << 10;
        return mask;
    }

    private ulong BuildMicrosoftVideoFormatMask()
    {
        ulong mask = 0;
        Add(0, 1920, 1280);
        Add(3, 2160, 1440);
        Add(6, 2256, 1504);
        Add(9, 2736, 1824);
        Add(12, 3000, 2000);
        Add(15, 3240, 2160);
        Add(18, 4500, 3000);
        return mask;

        void Add(int bit, int width, int height)
        {
            // These are Surface-shaped display modes, not decoder limits. Do
            // not expose every mode merely because the decoder can accept it:
            // Windows otherwise treats 3240x2160 as the largest display mode
            // and ignores the actual wide-wall timing from EDID.
            if (NativeWidth == width && NativeHeight == height)
                mask |= 1UL << bit;
        }
    }

    private static class SyntheticEdid
    {
        private const int EdidLength = 128;
        private const int DetailedTimingOffset = 54;

        public static byte[]? TryCreate(
            int width,
            int height,
            int nativeWidth,
            int nativeHeight,
            int refreshRate)
        {
            const int horizontalBlanking = 80;
            const int verticalBlanking = 40;
            var pixelClock10Khz = checked((int)Math.Ceiling(
                ((width + horizontalBlanking) * (long)(height + verticalBlanking) * refreshRate)
                / 10_000d));
            if (pixelClock10Khz is <= 0 or > 0x00ffffff)
                return null;

            var useBaseDetailedTiming = width <= 0x0fff
                && height <= 0x0fff
                && pixelClock10Khz <= ushort.MaxValue;
            var serialNumber = ((uint)height << 16) | (uint)width;
            var edid = new byte[useBaseDetailedTiming ? EdidLength : EdidLength * 2];
            byte[] header = [0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00];
            header.CopyTo(edid, 0);

            // Manufacturer "MWL", product 1, serial 1.
            var manufacturer = (ushort)((13 << 10) | (23 << 5) | 12);
            BinaryPrimitives.WriteUInt16BigEndian(edid.AsSpan(8, 2), manufacturer);
            // Product code 2 intentionally differs from the first synthetic
            // EDID implementation so Windows does not reuse its cached modes.
            BinaryPrimitives.WriteUInt16LittleEndian(edid.AsSpan(10, 2), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(edid.AsSpan(12, 4), serialNumber);
            edid[16] = 1;
            edid[17] = 36; // 2026
            edid[18] = 1;
            edid[19] = 4;
            edid[20] = 0x80; // digital input
            edid[21] = (byte)Math.Clamp((int)Math.Round(width / 96d * 2.54), 1, 255);
            edid[22] = (byte)Math.Clamp((int)Math.Round(height / 96d * 2.54), 1, 255);
            edid[23] = 120; // gamma 2.2
            // The first base-block detailed timing is always a usable
            // preferred mode. For very wide walls it is the native mode of
            // one output; the exact wall timing is carried by DisplayID.
            edid[24] = 0x06; // sRGB and preferred timing
            // Standard sRGB chromaticities. Advertising sRGB with zeroed
            // primaries makes some Windows display drivers reject the EDID.
            byte[] srgbChromaticities =
            [
                0xee, 0x91, 0xa3, 0x54, 0x4c,
                0x99, 0x26, 0x0f, 0x50, 0x54,
            ];
            srgbChromaticities.CopyTo(edid, 25);

            WriteStandardTimings(edid, nativeWidth, nativeHeight);

            if (useBaseDetailedTiming)
            {
                WriteDetailedTiming(
                    edid.AsSpan(DetailedTimingOffset, 18),
                    width,
                    height,
                    horizontalBlanking,
                    verticalBlanking,
                    pixelClock10Khz);
                WriteMirrorFallbackTiming(edid.AsSpan(72, 18), nativeWidth, nativeHeight, refreshRate);
                WriteTextDescriptor(edid.AsSpan(90, 18), 0xfc, "Multiwall");
                WriteRangeDescriptor(edid.AsSpan(108, 18), pixelClock10Khz);
                edid[126] = 0;
            }
            else
            {
                WritePreferredNativeTiming(
                    edid.AsSpan(DetailedTimingOffset, 18),
                    nativeWidth,
                    nativeHeight,
                    refreshRate);
                WriteMirrorFallbackTiming(edid.AsSpan(72, 18), nativeWidth, nativeHeight, refreshRate);
                WriteTextDescriptor(edid.AsSpan(90, 18), 0xfc, "Multiwall");
                WriteRangeDescriptor(edid.AsSpan(108, 18), pixelClock10Khz);
                edid[126] = 1;
                WriteDisplayId2Extension(
                    edid.AsSpan(EdidLength, EdidLength),
                    width,
                    height,
                    nativeWidth,
                    nativeHeight,
                    refreshRate,
                    serialNumber);
            }

            SetEdidBlockChecksum(edid.AsSpan(0, EdidLength));
            return edid;
        }

        private static void WritePreferredNativeTiming(
            Span<byte> descriptor,
            int width,
            int height,
            int refreshRate)
        {
            const int horizontalBlanking = 80;
            const int verticalBlanking = 40;
            var pixelClock10Khz = checked((int)Math.Ceiling(
                ((width + horizontalBlanking) * (long)(height + verticalBlanking) * refreshRate)
                / 10_000d));
            if (width <= 0x0fff && height <= 0x0fff && pixelClock10Khz <= ushort.MaxValue)
            {
                WriteDetailedTiming(
                    descriptor,
                    width,
                    height,
                    horizontalBlanking,
                    verticalBlanking,
                    pixelClock10Khz);
                return;
            }

            WriteMirrorFallbackTiming(descriptor, width, height, refreshRate);
        }

        private static void WriteStandardTimings(byte[] edid, int maximumWidth, int maximumHeight)
        {
            (int Width, int Height, int Aspect)[] modes =
            [
                (1920, 1080, 3),
                (1680, 1050, 0),
                (1600, 900, 3),
                (1440, 900, 0),
                (1280, 1024, 2),
                (1280, 720, 3),
                (1024, 768, 1),
                (800, 600, 1),
            ];

            var slot = 0;
            foreach (var mode in modes)
            {
                if (mode.Width > maximumWidth || mode.Height > maximumHeight)
                    continue;
                var offset = 38 + (slot * 2);
                edid[offset] = checked((byte)((mode.Width / 8) - 31));
                edid[offset + 1] = (byte)((mode.Aspect << 6) | 0); // 60 Hz
                slot++;
            }
            while (slot < 8)
            {
                var offset = 38 + (slot * 2);
                edid[offset] = 0x01;
                edid[offset + 1] = 0x01;
                slot++;
            }
        }

        private static void WriteMirrorFallbackTiming(
            Span<byte> descriptor,
            int maximumWidth,
            int maximumHeight,
            int refreshRate)
        {
            if (maximumWidth >= 1920 && maximumHeight >= 1080)
            {
                WriteTiming(descriptor, 1920, 1080, refreshRate);
                return;
            }
            if (maximumWidth >= 1280 && maximumHeight >= 720)
            {
                WriteTiming(descriptor, 1280, 720, refreshRate);
                return;
            }
            WriteTextDescriptor(descriptor, 0xfe, "Mirror mode");
        }

        private static void WriteTiming(Span<byte> descriptor, int width, int height, int refreshRate)
        {
            const int horizontalBlanking = 80;
            const int verticalBlanking = 40;
            var pixelClock10Khz = checked((int)Math.Ceiling(
                ((width + horizontalBlanking) * (long)(height + verticalBlanking) * refreshRate)
                / 10_000d));
            WriteDetailedTiming(
                descriptor,
                width,
                height,
                horizontalBlanking,
                verticalBlanking,
                pixelClock10Khz);
        }

        private static void WriteDisplayId2Extension(
            Span<byte> extension,
            int width,
            int height,
            int nativeWidth,
            int nativeHeight,
            int refreshRate,
            uint serialNumber)
        {
            const int displayIdHeaderOffset = 1;
            const int dataBlockOffset = 5;
            const int productPayloadLength = 21;
            const int displayParametersPayloadLength = 29;
            const int timingLength = 20;
            const int interfaceFeaturesPayloadLength = 9;
            var hasSeparateWallTiming = width != nativeWidth || height != nativeHeight;
            var timingPayloadLength = hasSeparateWallTiming ? timingLength * 2 : timingLength;
            var productBlockLength = 3 + productPayloadLength;
            var displayParametersBlockLength = 3 + displayParametersPayloadLength;
            var timingBlockLength = 3 + timingPayloadLength;
            var interfaceFeaturesBlockLength = 3 + interfaceFeaturesPayloadLength;
            var displayIdPayloadLength = productBlockLength
                + displayParametersBlockLength
                + timingBlockLength
                + interfaceFeaturesBlockLength;
            var displayIdChecksumOffset = displayIdHeaderOffset + 4 + displayIdPayloadLength;

            extension.Clear();
            extension[0] = 0x70; // DisplayID extension
            extension[displayIdHeaderOffset] = 0x20; // DisplayID 2.0
            extension[displayIdHeaderOffset + 1] = checked((byte)displayIdPayloadLength);
            extension[displayIdHeaderOffset + 2] = 4; // desktop productivity display
            extension[displayIdHeaderOffset + 3] = 0;

            var offset = dataBlockOffset;
            WriteDisplayIdProductIdentification(
                extension.Slice(offset, productBlockLength),
                serialNumber);
            offset += productBlockLength;

            WriteDisplayIdDisplayParameters(
                extension.Slice(offset, displayParametersBlockLength),
                width,
                height,
                nativeWidth,
                nativeHeight);
            offset += displayParametersBlockLength;

            extension[offset] = 0x22; // Type VII detailed timings
            extension[offset + 1] = 0x00;
            extension[offset + 2] = checked((byte)timingPayloadLength);
            offset += 3;
            // The first mode block is the native/preferred mode. This is what
            // Windows labels as Recommended in Duplicate mode.
            WriteDisplayIdTiming(
                extension.Slice(offset, timingLength),
                nativeWidth,
                nativeHeight,
                refreshRate,
                preferred: true);
            offset += timingLength;
            if (hasSeparateWallTiming)
            {
                // The complete wall is an additional mode and therefore the
                // maximum available resolution in Extend mode.
                WriteDisplayIdTiming(
                    extension.Slice(offset, timingLength),
                    width,
                    height,
                    refreshRate,
                    preferred: false);
                offset += timingLength;
            }

            WriteDisplayIdInterfaceFeatures(
                extension.Slice(offset, interfaceFeaturesBlockLength));

            var displayIdSum = 0;
            for (var index = displayIdHeaderOffset; index < displayIdChecksumOffset; index++)
                displayIdSum += extension[index];
            extension[displayIdChecksumOffset] = unchecked((byte)(0 - displayIdSum));
            SetEdidBlockChecksum(extension);
        }

        private static void WriteDisplayIdProductIdentification(Span<byte> block, uint serialNumber)
        {
            const string productName = "Multiwall";
            block.Clear();
            block[0] = 0x20;
            block[1] = 0x00;
            block[2] = checked((byte)(12 + productName.Length));
            block[3] = 0x02; // locally administered vendor identifier
            BinaryPrimitives.WriteUInt16LittleEndian(block[6..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(block[8..], serialNumber);
            block[12] = 1;
            block[13] = 26; // 2026
            block[14] = checked((byte)productName.Length);
            Encoding.ASCII.GetBytes(productName, block[15..]);
        }

        private static void WriteDisplayIdDisplayParameters(
            Span<byte> block,
            int wallWidth,
            int wallHeight,
            int nativeWidth,
            int nativeHeight)
        {
            block.Clear();
            block[0] = 0x21;
            block[1] = 0x80; // physical size is expressed in millimetres
            block[2] = 29;
            BinaryPrimitives.WriteUInt16LittleEndian(
                block[3..],
                checked((ushort)Math.Clamp((int)Math.Round(wallWidth / 96d * 25.4), 1, ushort.MaxValue)));
            BinaryPrimitives.WriteUInt16LittleEndian(
                block[5..],
                checked((ushort)Math.Clamp((int)Math.Round(wallHeight / 96d * 25.4), 1, ushort.MaxValue)));
            BinaryPrimitives.WriteUInt16LittleEndian(block[7..], checked((ushort)nativeWidth));
            BinaryPrimitives.WriteUInt16LittleEndian(block[9..], checked((ushort)nativeHeight));

            WriteChromaticity(block.Slice(12, 3), 0.6400, 0.3300);
            WriteChromaticity(block.Slice(15, 3), 0.3000, 0.6000);
            WriteChromaticity(block.Slice(18, 3), 0.1500, 0.0600);
            WriteChromaticity(block.Slice(21, 3), 0.3127, 0.3290);
            BinaryPrimitives.WriteUInt16LittleEndian(block[24..], 0x8000);
            BinaryPrimitives.WriteUInt16LittleEndian(block[26..], 0x8000);
            BinaryPrimitives.WriteUInt16LittleEndian(block[28..], 0x8000);
            block[30] = 0x11; // 8 bpc, active-matrix LCD
            block[31] = 120; // gamma 2.2
        }

        private static void WriteChromaticity(Span<byte> destination, double x, double y)
        {
            var encodedX = Math.Clamp((int)Math.Round(x * 4096), 0, 0x0fff);
            var encodedY = Math.Clamp((int)Math.Round(y * 4096), 0, 0x0fff);
            destination[0] = (byte)encodedX;
            destination[1] = (byte)(((encodedY & 0x0f) << 4) | (encodedX >> 8));
            destination[2] = (byte)(encodedY >> 4);
        }

        private static void WriteDisplayIdTiming(
            Span<byte> timing,
            int width,
            int height,
            int refreshRate,
            bool preferred)
        {
            const int horizontalBlanking = 80;
            const int verticalBlanking = 40;
            const int horizontalSyncOffset = 8;
            const int horizontalSyncWidth = 32;
            const int verticalSyncOffset = 3;
            const int verticalSyncWidth = 8;
            var pixelClock10Khz = checked((int)Math.Ceiling(
                ((width + horizontalBlanking) * (long)(height + verticalBlanking) * refreshRate)
                / 10_000d));
            var storedPixelClock = checked((pixelClock10Khz * 10) - 1);
            if (storedPixelClock > 0x00ffffff)
                throw new ArgumentOutOfRangeException(nameof(width));

            timing.Clear();
            timing[0] = (byte)storedPixelClock;
            timing[1] = (byte)(storedPixelClock >> 8);
            timing[2] = (byte)(storedPixelClock >> 16);
            timing[3] = preferred ? (byte)0x88 : (byte)0x08;
            BinaryPrimitives.WriteUInt16LittleEndian(timing[4..], checked((ushort)(width - 1)));
            BinaryPrimitives.WriteUInt16LittleEndian(timing[6..], horizontalBlanking - 1);
            BinaryPrimitives.WriteUInt16LittleEndian(
                timing[8..],
                (ushort)(0x8000 | (horizontalSyncOffset - 1)));
            BinaryPrimitives.WriteUInt16LittleEndian(timing[10..], horizontalSyncWidth - 1);
            BinaryPrimitives.WriteUInt16LittleEndian(timing[12..], checked((ushort)(height - 1)));
            BinaryPrimitives.WriteUInt16LittleEndian(timing[14..], verticalBlanking - 1);
            BinaryPrimitives.WriteUInt16LittleEndian(
                timing[16..],
                (ushort)(0x8000 | (verticalSyncOffset - 1)));
            BinaryPrimitives.WriteUInt16LittleEndian(timing[18..], verticalSyncWidth - 1);
        }

        private static void WriteDisplayIdInterfaceFeatures(Span<byte> block)
        {
            block.Clear();
            block[0] = 0x26;
            block[1] = 0x00;
            block[2] = 9;
            block[3] = 0x02; // RGB, 8 bits per component
            block[9] = 0x01; // sRGB transfer function and color space
        }

        private static void SetEdidBlockChecksum(Span<byte> block)
        {
            var sum = 0;
            for (var index = 0; index < EdidLength - 1; index++)
                sum += block[index];
            block[EdidLength - 1] = unchecked((byte)(0 - sum));
        }

        private static void WriteDetailedTiming(
            Span<byte> descriptor,
            int width,
            int height,
            int horizontalBlanking,
            int verticalBlanking,
            int pixelClock10Khz)
        {
            const int horizontalSyncOffset = 8;
            const int horizontalSyncWidth = 32;
            const int verticalSyncOffset = 3;
            const int verticalSyncWidth = 8;

            BinaryPrimitives.WriteUInt16LittleEndian(descriptor, (ushort)pixelClock10Khz);
            descriptor[2] = (byte)width;
            descriptor[3] = (byte)horizontalBlanking;
            descriptor[4] = (byte)(((width >> 8) << 4) | (horizontalBlanking >> 8));
            descriptor[5] = (byte)height;
            descriptor[6] = (byte)verticalBlanking;
            descriptor[7] = (byte)(((height >> 8) << 4) | (verticalBlanking >> 8));
            descriptor[8] = horizontalSyncOffset;
            descriptor[9] = horizontalSyncWidth;
            descriptor[10] = (verticalSyncOffset << 4) | verticalSyncWidth;
            descriptor[11] = 0;

            var widthMillimeters = Math.Clamp((int)Math.Round(width / 96d * 25.4), 1, 0x0fff);
            var heightMillimeters = Math.Clamp((int)Math.Round(height / 96d * 25.4), 1, 0x0fff);
            descriptor[12] = (byte)widthMillimeters;
            descriptor[13] = (byte)heightMillimeters;
            descriptor[14] = (byte)(((widthMillimeters >> 8) << 4) | (heightMillimeters >> 8));
            descriptor[17] = 0x1e; // progressive, separate positive H/V sync
        }

        private static void WriteTextDescriptor(Span<byte> descriptor, byte type, string text)
        {
            descriptor.Clear();
            descriptor[3] = type;
            var payload = Encoding.ASCII.GetBytes((text + "\n").PadRight(13));
            payload.AsSpan(0, 13).CopyTo(descriptor[5..]);
        }

        private static void WriteRangeDescriptor(Span<byte> descriptor, int pixelClock10Khz)
        {
            descriptor.Clear();
            descriptor[3] = 0xfd;
            descriptor[5] = 24;
            descriptor[6] = 60;
            descriptor[7] = 15;
            descriptor[8] = 255;
            descriptor[9] = (byte)Math.Clamp((pixelClock10Khz + 999) / 1000, 1, 255);
        }
    }
}
