using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using ShadowDisk.Core.Health;

namespace ShadowDisk.Windows;

/// <summary>
/// Reads a whole disk through \\.\PhysicalDriveN, opened for READING ONLY (FileAccess.Read) and without the
/// Windows cache (FILE_FLAG_NO_BUFFERING), so every read really comes from the drive and its time is real.
/// There is no write call in this class. Needs administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PhysicalDriveReader : IRawDiskReader
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const int Align = 4096;
    private readonly SafeFileHandle _handle;
    private readonly byte[] _memory;
    private readonly int _start;
    private readonly int _bufferSize;

    public PhysicalDriveReader(int diskNumber, long sizeBytes, int sectorSize, int maxBlockBytes)
    {
        SizeBytes = sizeBytes;
        SectorSize = Math.Max(512, sectorSize);
        _bufferSize = maxBlockBytes;
        // Unbuffered reads need a buffer whose address is sector-aligned: pin an array and start at a 4 KiB boundary.
        _memory = GC.AllocateUninitializedArray<byte>(maxBlockBytes + Align, pinned: true);
        var address = Marshal.UnsafeAddrOfPinnedArrayElement(_memory, 0).ToInt64();
        _start = (int)((Align - address % Align) % Align);
        _handle = File.OpenHandle($@"\\.\PhysicalDrive{diskNumber}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, NoBuffering);
    }

    public long SizeBytes { get; }
    public int SectorSize { get; }

    public string? Read(long offset, int length)
    {
        if (length > _bufferSize) throw new ArgumentOutOfRangeException(nameof(length));
        try
        {
            var done = 0;
            while (done < length)
            {
                var n = RandomAccess.Read(_handle, _memory.AsSpan(_start + done, length - done), offset + done);
                if (n <= 0) return "The drive returned no data for this area.";
                done += n;
            }
            return null;
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
    }

    public void Dispose() => _handle.Dispose();
}
