using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using ShadowDisk.Core.Moving;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;

namespace ShadowDisk.Windows;

/// <summary>
/// Windows side of "Move partition": volume lock and dismount, NTFS's used-space map, raw disk access limited to the
/// move's area, and the partition-table changes (hide while moving; new start and original type at the end).
/// Every call runs once. Needs administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMoveHost : IMoveHost
{
    /// <summary>GPT type used while a partition is moving. Windows does not mount partitions of unknown types.</summary>
    public static readonly Guid MovingGptType = new("5d1f6a7e-2b9c-4c3e-9a51-534841444f57");

    /// <summary>MBR type used while moving (0x7F: reserved, never mounted by Windows).</summary>
    public const byte MovingMbrType = 0x7F;

    private SafeFileHandle? _volume;

    public Task<(IReadOnlyList<ByteRange>? Used, string? Error)> TakeOfflineAsync(MoveJournal j, bool readUsedMap, CancellationToken ct) =>
        Task.Run<(IReadOnlyList<ByteRange>?, string?)>(() =>
        {
            if (!Elevation.IsElevated()) return (null, "Administrator rights are needed.");
            if (!readUsedMap) return ([], null);   // resuming: the partition is hidden, so there is no mounted volume to lock
            return Inspect(j, keepOffline: true);
        }, ct);

    public Task<string?> CheckAsync(MoveJournal j, CancellationToken ct) =>
        Task.Run(() => Elevation.IsElevated() ? Inspect(j, keepOffline: false).Item2 : "Administrator rights are needed.", ct);

    /// <summary>
    /// Opens and locks the volume, checks it is the chosen partition and reads NTFS's map of used space.
    /// With <paramref name="keepOffline"/> it is then dismounted and the lock kept; otherwise the lock is let go (a dry run).
    /// </summary>
    private (IReadOnlyList<ByteRange>?, string?) Inspect(MoveJournal j, bool keepOffline)
    {
        var letter = j.DriveLetter;
        if (letter is not { Length: 1 }) return (null, "The volume has no drive letter.");

        var volume = Native.Open($@"\\.\{letter}:", write: true, unbuffered: false);
        if (volume.IsInvalid) return (null, $"{letter}: could not be opened ({Native.LastError()}).");
        try
        {
            // Make sure the letter is really the partition being moved.
            var extents = Native.Ioctl(volume, Native.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 256);
            if (extents is null || BitConverter.ToInt32(extents, 0) != 1
                || BitConverter.ToInt32(extents, 8) != j.DiskNumber
                || BitConverter.ToInt64(extents, 16) != j.OldOffset
                || BitConverter.ToInt64(extents, 24) != j.SizeBytes)
                return Fail(volume, $"{letter}: is not the partition that was chosen (or it spans several disks).");

            // 1) While it is still mounted, ask NTFS for its used-space map (an extra safety net; may be refused).
            List<ByteRange>? mountedView = null;
            var (ntfs, returned, error) = Native.IoctlRaw(volume, Native.FSCTL_GET_NTFS_VOLUME_DATA, null, 1024);
            if ((error == 0 || error == Native.ERROR_MORE_DATA) && returned >= 48 && BitConverter.ToInt32(ntfs, 44) is > 0 and var clusterSize)
                mountedView = ReadUsedClusters(volume, clusterSize, out _);

            // 2) Lock it: refused while any file is open. Windows flushes everything to the disk.
            if (Native.Ioctl(volume, Native.FSCTL_LOCK_VOLUME, null, 0) is null)
                return Fail(volume, $"{letter}: is in use ({Native.LastError()}). Close open files, File Explorer windows and programs that use it, then try again.");

            // 3) For a real move, dismount it ("not ready" means Windows already did that on the lock).
            if (keepOffline)
            {
                var (_, _, dismountError) = Native.IoctlRaw(volume, Native.FSCTL_DISMOUNT_VOLUME, null, 0);
                if (dismountError != 0 && dismountError != Native.ERROR_NOT_READY)
                    return Fail(volume, $"{letter}: could not be taken offline (Windows error {dismountError}: {new Win32Exception(dismountError).Message}).");
            }

            // 4) Read NTFS's own $Bitmap straight from the disk (read-only). With the volume locked it can't change.
            List<ByteRange>? onDisk;
            string? bitmapError;
            using (var disk = Native.Open($@"\\.\PhysicalDrive{j.DiskNumber.ToString(CultureInfo.InvariantCulture)}", write: false, unbuffered: false))
            {
                if (disk.IsInvalid) return Fail(volume, $"Disk {j.DiskNumber} could not be opened for reading ({Native.LastError()}).");
                (onDisk, bitmapError) = NtfsBitmap.Read((offset, buffer) =>
                {
                    var done = 0;
                    while (done < buffer.Length)
                    {
                        var n = RandomAccess.Read(disk, buffer[done..], j.OldOffset + offset + done);
                        if (n <= 0) throw new IOException("The disk returned no data.");
                        done += n;
                    }
                }, Math.Max(512, j.SectorSize), j.SizeBytes);
            }
            if (onDisk is null) return Fail(volume, $"NTFS's map of used space on {letter}: could not be read from the disk ({bitmapError}).");

            // Anything either view calls "used" is moved (moving a free cluster is harmless; missing a used one is not).
            IReadOnlyList<ByteRange> used = mountedView is null ? onDisk : [.. onDisk, .. mountedView];

            if (!keepOffline)
            {
                volume.Dispose();   // dry run: closing the handle lets go of the lock; nothing was changed
                return (used, null);
            }

            _volume = volume;   // keep the lock until the move is over
            return (used, null);
        }
        catch
        {
            volume.Dispose();
            throw;
        }
    }

    private static (IReadOnlyList<ByteRange>?, string?) Fail(SafeFileHandle volume, string? message)
    {
        volume.Dispose();   // closing the handle also releases a lock
        return (null, message);
    }

    /// <summary>Reads NTFS's cluster bitmap and returns the used runs as byte ranges relative to the partition start.</summary>
    private static List<ByteRange>? ReadUsedClusters(SafeFileHandle volume, int clusterSize, out int lastError)
    {
        lastError = 0;
        var runs = new List<ByteRange>();
        long next = 0;
        long? runStart = null;
        const int OutSize = 1024 * 1024 + 16;
        while (true)
        {
            var input = BitConverter.GetBytes(next);
            var (output, returned, error) = Native.IoctlRaw(volume, Native.FSCTL_GET_VOLUME_BITMAP, input, OutSize);
            if (error != 0 && error != Native.ERROR_MORE_DATA) { lastError = error; return null; }
            var startLcn = BitConverter.ToInt64(output, 0);
            var bitmapSize = BitConverter.ToInt64(output, 8);   // clusters from startLcn to the end
            var bits = Math.Min(bitmapSize, (long)(returned - 16) * 8);
            for (long i = 0; i < bits; i++)
            {
                var used = (output[16 + (i >> 3)] & (1 << (int)(i & 7))) != 0;
                var lcn = startLcn + i;
                if (used && runStart is null) runStart = lcn;
                else if (!used && runStart is { } s)
                {
                    runs.Add(new ByteRange(s * clusterSize, (lcn - s) * clusterSize));
                    runStart = null;
                }
            }
            next = startLcn + bits;
            if (error == 0 || bits == 0) break;
        }
        if (runStart is { } open) runs.Add(new ByteRange(open * clusterSize, (next - open) * clusterSize));
        return runs;
    }

    public Task<BackendResult> HideAsync(MoveJournal j, MoveJournalStore store, CancellationToken ct) => Task.Run(() =>
        EditLayout(j, (layout, entry) =>
        {
            if (j.Style == PartitionStyle.Gpt)
            {
                var type = layout.GptType(entry);
                if (type == MovingGptType) return true;   // already hidden (resume)
                j.OriginalGptType = type.ToString("B");
                j.PartitionGuid = layout.GptId(entry).ToString("B");
                store.Save(j);                               // remember the original type BEFORE changing it
                layout.SetGptType(entry, MovingGptType);
            }
            else
            {
                var type = layout.MbrType(entry);
                if (type == MovingMbrType) return true;
                j.OriginalMbrType = type;
                store.Save(j);
                layout.SetMbrType(entry, MovingMbrType);
            }
            return false;
        }, findAt: j.OldOffset), ct);

    public IMoveDevice OpenDevice(MoveJournal j) => new WindowsMoveDevice(j);

    public Task<BackendResult> FinishAsync(MoveJournal j, CancellationToken ct) => Task.Run(() =>
    {
        var result = EditLayout(j, (layout, entry) =>
        {
            var atNew = layout.StartingOffset(entry) == j.NewOffset;
            if (j.Style == PartitionStyle.Gpt)
            {
                var original = Guid.Parse(j.OriginalGptType ?? throw new InvalidOperationException("The journal has no original partition type."));
                if (atNew && layout.GptType(entry) == original) return true;   // already finished
                layout.SetStartingOffset(entry, j.NewOffset);
                layout.SetGptType(entry, original);
            }
            else
            {
                var original = (byte)(j.OriginalMbrType ?? throw new InvalidOperationException("The journal has no original partition type."));
                if (atNew && layout.MbrType(entry) == original) return true;
                layout.SetStartingOffset(entry, j.NewOffset);
                layout.SetMbrType(entry, original);
                layout.SetMbrHiddenSectors(entry, (uint)(j.NewOffset / j.SectorSize));
                layout.SetMbrRecognized(entry);
            }
            return false;
        }, findAt: j.OldOffset, alsoAt: j.NewOffset);
        if (!result.Done) return result;
        Release();
        RestoreLetter(j);
        return result;
    }, ct);

    /// <summary>Reads the partition table, lets <paramref name="change"/> edit one entry, writes it back once and re-reads it.</summary>
    private static BackendResult EditLayout(MoveJournal j, Func<DriveLayout, int, bool> change, long findAt, long? alsoAt = null)
    {
        if (!Elevation.IsElevated()) return new(false, "Administrator rights are needed.");
        using var disk = Native.Open($@"\\.\PhysicalDrive{j.DiskNumber.ToString(CultureInfo.InvariantCulture)}", write: true, unbuffered: false);
        if (disk.IsInvalid) return new(false, $"Disk {j.DiskNumber} could not be opened ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");

        var raw = Native.Ioctl(disk, Native.IOCTL_DISK_GET_DRIVE_LAYOUT_EX, null, 48 + 144 * 128);
        if (raw is null) return new(false, "The partition table could not be read.");
        var layout = new DriveLayout(raw);
        if (layout.Style != (j.Style == PartitionStyle.Gpt ? 1 : 0)) return new(false, "The disk's partition style is not what the journal says.");
        var entry = layout.Find(j.PartitionGuid, findAt, alsoAt);
        if (entry < 0) return new(false, "The partition was not found in the partition table.");
        if (layout.Length(entry) != j.SizeBytes) return new(false, "The partition in the table has a different size than the journal says.");

        if (change(layout, entry)) return new(true, null);   // nothing to change
        layout.MarkAllForRewrite();
        if (Native.Ioctl(disk, Native.IOCTL_DISK_SET_DRIVE_LAYOUT_EX, layout.Used, 0) is null)
            return new(false, "Windows refused the partition table change: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        Native.Ioctl(disk, Native.IOCTL_DISK_UPDATE_PROPERTIES, null, 0);
        return new(true, null);
    }

    /// <summary>After the move Windows mounts the volume again; give it back its letter if it came back without one.</summary>
    private static void RestoreLetter(MoveJournal j)
    {
        if (j.DriveLetter is not { Length: 1 } letter) return;
        for (var attempt = 0; attempt < 20; attempt++)   // waiting for Windows to mount it (this is waiting, not retrying a change)
        {
            Thread.Sleep(500);
            Wmi.RefreshStorageCache();
            var scope = Wmi.StorageScope();
            var partition = Wmi.All(scope, "MSFT_Partition").FirstOrDefault(p =>
                Wmi.ToInt(p["DiskNumber"]) == j.DiskNumber && Convert.ToInt64(p["Offset"], CultureInfo.InvariantCulture) == j.NewOffset);
            if (partition is null) continue;
            var current = partition["DriveLetter"] is char c && char.IsLetter(c) ? char.ToUpperInvariant(c).ToString() : null;
            if (string.Equals(current, letter, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists($@"{letter}:\")) return;   // the old letter is taken by something else; leave it to the user
            if (current is not null)   // Windows gave it a new letter: swap it back to the one it had
                Wmi.Invoke(partition, "RemoveAccessPath", p => p["AccessPath"] = $@"{current}:\");
            Wmi.Invoke(partition, "AddAccessPath", p => p["AccessPath"] = $@"{letter}:\");
            return;
        }
    }

    public void Release()
    {
        _volume?.Dispose();
        _volume = null;
    }
}

/// <summary>
/// Raw disk access for one move, refusing anything outside the move's area (old place ∪ new place).
/// Unbuffered and write-through, so a flush really means "on the disk".
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsMoveDevice : IMoveDevice
{
    private const int Align = 4096;
    private readonly SafeFileHandle _disk;
    private readonly long _areaStart;
    private readonly long _areaEnd;
    private readonly byte[] _memory;
    private readonly int _start;

    public WindowsMoveDevice(MoveJournal j)
    {
        SectorSize = j.SectorSize;
        _areaStart = Math.Min(j.OldOffset, j.NewOffset);
        _areaEnd = Math.Max(j.OldOffset, j.NewOffset) + j.SizeBytes;
        _memory = GC.AllocateUninitializedArray<byte>(Math.Max(j.ChunkBytes, j.SectorSize) + Align, pinned: true);
        var address = Marshal.UnsafeAddrOfPinnedArrayElement(_memory, 0).ToInt64();
        _start = (int)((Align - address % Align) % Align);
        _disk = Native.Open($@"\\.\PhysicalDrive{j.DiskNumber.ToString(CultureInfo.InvariantCulture)}", write: true, unbuffered: true);
        if (_disk.IsInvalid) throw new IOException($"Disk {j.DiskNumber} could not be opened: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    public int SectorSize { get; }

    private void Guard(long offset, int length)
    {
        if (offset < _areaStart || offset + length > _areaEnd || offset % SectorSize != 0 || length % SectorSize != 0)
            throw new InvalidOperationException("Refused: outside the area of this move.");
    }

    public void Read(long diskOffset, Span<byte> buffer)
    {
        Guard(diskOffset, buffer.Length);
        var span = _memory.AsSpan(_start, buffer.Length);
        var done = 0;
        while (done < buffer.Length)
        {
            var n = RandomAccess.Read(_disk, span[done..], diskOffset + done);
            if (n <= 0) throw new IOException("The disk returned no data.");
            done += n;
        }
        span.CopyTo(buffer);
    }

    public void Write(long diskOffset, ReadOnlySpan<byte> data)
    {
        Guard(diskOffset, data.Length);
        var span = _memory.AsSpan(_start, data.Length);
        data.CopyTo(span);
        RandomAccess.Write(_disk, span, diskOffset);
    }

    public void Flush()
    {
        if (!Native.FlushFileBuffers(_disk)) throw new IOException("The disk could not be flushed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    public void Dispose() => _disk.Dispose();
}

/// <summary>A DRIVE_LAYOUT_INFORMATION_EX in a byte array (48-byte header, then 144-byte PARTITION_INFORMATION_EX entries).</summary>
internal sealed class DriveLayout(byte[] bytes)
{
    private const int Header = 48;
    private const int EntrySize = 144;

    public byte[] Bytes { get; } = bytes;
    public int Style => BitConverter.ToInt32(Bytes, 0);              // 0 = MBR, 1 = GPT
    public int Count => BitConverter.ToInt32(Bytes, 4);
    private static int At(int entry) => Header + entry * EntrySize;

    public long StartingOffset(int e) => BitConverter.ToInt64(Bytes, At(e) + 8);
    public long Length(int e) => BitConverter.ToInt64(Bytes, At(e) + 16);
    public void SetStartingOffset(int e, long value) => BitConverter.TryWriteBytes(Bytes.AsSpan(At(e) + 8, 8), value);
    public Guid GptType(int e) => new(Bytes.AsSpan(At(e) + 32, 16));
    public Guid GptId(int e) => new(Bytes.AsSpan(At(e) + 48, 16));
    public void SetGptType(int e, Guid type) => type.TryWriteBytes(Bytes.AsSpan(At(e) + 32, 16));
    public byte MbrType(int e) => Bytes[At(e) + 32];
    public void SetMbrType(int e, byte type) => Bytes[At(e) + 32] = type;
    public void SetMbrRecognized(int e) => Bytes[At(e) + 34] = 1;
    public byte[] Used => Bytes[..Math.Min(Bytes.Length, Header + Count * EntrySize)];
    public void SetMbrHiddenSectors(int e, uint sectors) => BitConverter.TryWriteBytes(Bytes.AsSpan(At(e) + 36, 4), sectors);

    public int Find(string? gptId, long offset, long? alsoAt)
    {
        for (var e = 0; e < Count && At(e) + EntrySize <= Bytes.Length; e++)
        {
            if (Length(e) == 0) continue;
            if (Style == 1 && gptId is not null && Guid.TryParse(gptId, out var id) && GptId(e) == id) return e;
            if (StartingOffset(e) == offset || (alsoAt is { } other && StartingOffset(e) == other)) return e;
        }
        return -1;
    }

    /// <summary>Windows only writes entries marked for rewrite; mark them all so the table is written as it is now.</summary>
    public void MarkAllForRewrite()
    {
        for (var e = 0; e < Count && At(e) + EntrySize <= Bytes.Length; e++) Bytes[At(e) + 28] = 1;
    }
}

[SupportedOSPlatform("windows")]
internal static partial class Native
{
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    public const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
    public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;
    public const uint IOCTL_DISK_SET_DRIVE_LAYOUT_EX = 0x0007C054;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_NOT_READY = 21;

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize,
        out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlushFileBuffers(SafeFileHandle file);

    public static SafeFileHandle Open(string path, bool write, bool unbuffered) =>
        CreateFileW(path, GENERIC_READ | (write ? GENERIC_WRITE : 0), FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            unbuffered ? FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH : 0, IntPtr.Zero);

    /// <summary>Runs one control code; returns the output (or an empty array), or null on failure.</summary>
    public static byte[]? Ioctl(SafeFileHandle h, uint code, byte[]? input, int outputSize)
    {
        var (output, _, error) = IoctlRaw(h, code, input, outputSize);
        return error == 0 ? output : null;
    }

    public static string LastError()
    {
        var code = Marshal.GetLastWin32Error();
        return $"Windows error {code}: {new Win32Exception(code).Message}";
    }

    public static (byte[] Output, int Returned, int Error) IoctlRaw(SafeFileHandle h, uint code, byte[]? input, int outputSize)
    {
        var output = new byte[Math.Max(0, outputSize)];
        var ok = DeviceIoControl(h, code, input, input?.Length ?? 0, outputSize > 0 ? output : null, outputSize, out var returned, IntPtr.Zero);
        return (output, returned, ok ? 0 : Marshal.GetLastWin32Error());
    }
}
