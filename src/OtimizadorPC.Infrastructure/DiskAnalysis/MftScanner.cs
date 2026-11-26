using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OtimizadorPC.Core.DiskAnalysis;

namespace OtimizadorPC.Infrastructure.DiskAnalysis;

/// <summary>
/// Motor ultra: lê a Master File Table do NTFS diretamente do volume (como o WizTree).
/// Em vez de abrir pasta por pasta, percorre os registros da MFT em blocos grandes — por isso
/// analisa discos inteiros em segundos. Exige administrador.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MftScanner
{
    private const int RootRecord = 5;
    private const uint AttrStandardInformation = 0x10;
    private const uint AttrFileName = 0x30;
    private const uint AttrData = 0x80;
    private const uint AttrEnd = 0xFFFFFFFF;
    private const int FixupStride = 512;
    private const long RefMask = 0x0000FFFFFFFFFFFF;

    public static volatile string? CurrentPhase;

    public static DirNode Scan(string requestedPath, ScanAggregator agg, CancellationToken ct)
    {
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(requestedPath))!; // "C:\"
        using var volume = OpenVolume(volumeRoot);
        var info = GetVolumeData(volume);
        var recordSize = (int)info.BytesPerFileRecordSegment;
        var clusterSize = (long)info.BytesPerCluster;

        // 1. Lê o registro 0 ($MFT) para saber onde estão os pedaços da própria MFT
        CurrentPhase = "Lendo a tabela de arquivos (MFT)";
        var first = new byte[Align(recordSize, (int)info.BytesPerSector)];
        RandomAccess.Read(volume, first, info.MftStartLcn * clusterSize);
        if (!ApplyFixup(first.AsSpan(0, recordSize))) throw new InvalidDataException("Registro inicial da MFT inválido.");
        var (runs, mftBytes) = ReadMftDataRuns(first.AsSpan(0, recordSize));
        var totalRecords = (int)(Math.Min(mftBytes, info.MftValidDataLength) / recordSize);

        var names = new string?[totalRecords];
        var parents = new int[totalRecords];
        var sizes = new long[totalRecords];
        var modified = new long[totalRecords];
        var flags = new byte[totalRecords]; // 1 = em uso, 2 = pasta, 4 = nome não-DOS já definido

        // 2. Percorre a MFT em blocos de até 8 MB
        var buffer = new byte[Align(8 * 1024 * 1024, recordSize)];
        long recordIndex = 0;
        foreach (var (lcn, clusters) in runs)
        {
            var runOffset = lcn * clusterSize;
            var runBytes = clusters * clusterSize;
            for (long done = 0; done < runBytes && recordIndex < totalRecords; done += buffer.Length)
            {
                ct.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(buffer.Length, runBytes - done);
                var read = RandomAccess.Read(volume, buffer.AsSpan(0, toRead), runOffset + done);
                for (var pos = 0; pos + recordSize <= read && recordIndex < totalRecords; pos += recordSize, recordIndex++)
                    ParseRecord(buffer.AsSpan(pos, recordSize), (int)recordIndex, names, parents, sizes, modified, flags);
            }
        }

        // 3. Monta a árvore de pastas
        CurrentPhase = "Montando a árvore de pastas";
        var nodes = new DirNode?[totalRecords];
        var root = new DirNode(volumeRoot, null) { LastWrite = FromFileTime(modified[RootRecord]) };
        nodes[RootRecord] = root;
        var childLists = new Dictionary<int, List<DirNode>>();

        for (var i = 0; i < totalRecords; i++)
        {
            if (i == RootRecord || (flags[i] & 3) != 3 || names[i] is null) continue;
            nodes[i] = new DirNode(names[i]!, null) { LastWrite = FromFileTime(modified[i]) };
        }
        for (var i = 0; i < totalRecords; i++)
        {
            var node = nodes[i];
            if (node is null || i == RootRecord) continue;
            var p = parents[i];
            var parent = p >= 0 && p < totalRecords && p != i ? nodes[p] : null;
            if (parent is null) continue; // pasta órfã (removida no meio da leitura)
            node.Parent = parent;
            if (!childLists.TryGetValue(p, out var list)) childLists[p] = list = [];
            list.Add(node);
        }
        foreach (var (idx, list) in childLists) nodes[idx]!.SetChildren(list);

        // Se o usuário pediu uma subpasta, a análise fica restrita a ela
        var target = root;
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
        if (!string.Equals(normalized, Path.TrimEndingDirectorySeparator(volumeRoot), StringComparison.OrdinalIgnoreCase))
            target = root.Find(normalized) ?? throw new DirectoryNotFoundException(requestedPath);

        // 4. Distribui os arquivos nas pastas
        CurrentPhase = "Somando arquivos";
        for (var i = 0; i < totalRecords; i++)
        {
            if ((flags[i] & 3) != 1 || names[i] is null) continue;
            if (i % 65536 == 0) ct.ThrowIfCancellationRequested();
            var p = parents[i];
            if (p < 0 || p >= totalRecords) continue;
            var dir = nodes[p];
            if (dir is null || !IsInside(dir, target)) continue;

            var size = sizes[i];
            var written = FromFileTime(modified[i]);
            dir.FilesSize += size;
            dir.FileCount++;
            if (written > dir.LastWrite) dir.LastWrite = written;
            var name = names[i]!;
            var owner = dir;
            agg.AddFile(Path.GetExtension(name), size, written, () => Path.Combine(owner.FullPath, name));
        }
        agg.Folders = CountDirs(target);

        if (!ReferenceEquals(target, root))
        {
            target.Parent = null;
            target.Name = normalized;
        }
        return target;
    }

    private static bool IsInside(DirNode node, DirNode target)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (ReferenceEquals(n, target)) return true;
        return false;
    }

    private static long CountDirs(DirNode node)
    {
        long count = 1;
        foreach (var c in node.Children) count += CountDirs(c);
        return count;
    }

    // =====================================================================
    // Registros da MFT
    // =====================================================================

    private static void ParseRecord(Span<byte> rec, int index, string?[] names, int[] parents, long[] sizes, long[] modified, byte[] flags)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(rec) != 0x454C4946 /* "FILE" */) return;
        if (!ApplyFixup(rec)) return;

        var recordFlags = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x16..]);
        if ((recordFlags & 1) == 0) return; // registro livre

        var baseRef = (long)(BinaryPrimitives.ReadUInt64LittleEndian(rec[0x20..]) & RefMask);
        var target = baseRef != 0 ? (int)baseRef : index;
        if (target < 0 || target >= names.Length) return;
        if (baseRef == 0)
        {
            flags[target] |= 1;
            if ((recordFlags & 2) != 0) flags[target] |= 2;
        }

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x14..]);
        while (offset + 16 <= rec.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(rec[offset..]);
            if (type == AttrEnd || type == 0) break;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[(offset + 4)..]);
            if (length <= 0 || offset + length > rec.Length) break;
            var attr = rec.Slice(offset, length);
            var nonResident = attr[8] != 0;
            var nameLength = attr[9];

            switch (type)
            {
                case AttrStandardInformation when !nonResident:
                {
                    var v = attr[BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..])..];
                    if (v.Length >= 0x10) modified[target] = BinaryPrimitives.ReadInt64LittleEndian(v[0x08..]);
                    break;
                }
                case AttrFileName when !nonResident:
                {
                    var v = attr[BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..])..];
                    if (v.Length < 0x42) break;
                    var nameChars = v[0x40];
                    var ns = v[0x41];
                    if (v.Length < 0x42 + nameChars * 2) break;
                    var isDosOnly = ns == 2;
                    // Prefere o nome longo (Win32) ao nome curto 8.3
                    if (names[target] is null || (!isDosOnly && (flags[target] & 4) == 0))
                    {
                        names[target] = Encoding.Unicode.GetString(v.Slice(0x42, nameChars * 2));
                        parents[target] = (int)((long)BinaryPrimitives.ReadUInt64LittleEndian(v) & RefMask);
                        if (!isDosOnly) flags[target] |= 4;
                    }
                    break;
                }
                case AttrData when nameLength == 0: // fluxo de dados principal (sem nome)
                {
                    if (!nonResident)
                        sizes[target] = Math.Max(sizes[target], BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]));
                    else if (BinaryPrimitives.ReadUInt64LittleEndian(attr[0x10..]) == 0) // primeiro segmento (VCN 0)
                        sizes[target] = Math.Max(sizes[target], BinaryPrimitives.ReadInt64LittleEndian(attr[0x30..]));
                    break;
                }
            }
            offset += length;
        }
    }

    /// <summary>Corrige os 2 últimos bytes de cada setor de 512 B (proteção contra gravações parciais).</summary>
    private static bool ApplyFixup(Span<byte> rec)
    {
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x04..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x06..]);
        if (usaCount < 2 || usaOffset + usaCount * 2 > rec.Length) return false;
        var usn = BinaryPrimitives.ReadUInt16LittleEndian(rec[usaOffset..]);
        for (var i = 1; i < usaCount; i++)
        {
            var end = i * FixupStride - 2;
            if (end + 2 > rec.Length) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(rec[end..]) != usn) return false;
            rec[end] = rec[usaOffset + i * 2];
            rec[end + 1] = rec[usaOffset + i * 2 + 1];
        }
        return true;
    }

    /// <summary>Lista de pedaços (LCN, quantidade de clusters) do $DATA da própria MFT.</summary>
    private static (List<(long Lcn, long Clusters)> Runs, long DataSize) ReadMftDataRuns(Span<byte> rec)
    {
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x14..]);
        while (offset + 16 <= rec.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(rec[offset..]);
            if (type == AttrEnd) break;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[(offset + 4)..]);
            if (length <= 0) break;
            var attr = rec.Slice(offset, length);
            if (type == AttrData && attr[8] != 0 && attr[9] == 0)
            {
                var dataSize = BinaryPrimitives.ReadInt64LittleEndian(attr[0x30..]);
                var runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x20..]);
                return (DecodeRuns(attr[runsOffset..]), dataSize);
            }
            offset += length;
        }
        throw new InvalidDataException("Não foi possível localizar os dados da MFT.");
    }

    internal static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> data)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        var p = 0;
        while (p < data.Length && data[p] != 0)
        {
            var header = data[p++];
            var lenBytes = header & 0x0F;
            var offBytes = header >> 4;
            long length = 0;
            for (var i = 0; i < lenBytes; i++) length |= (long)data[p + i] << (8 * i);
            p += lenBytes;
            long delta = 0;
            for (var i = 0; i < offBytes; i++) delta |= (long)data[p + i] << (8 * i);
            if (offBytes > 0 && (data[p + offBytes - 1] & 0x80) != 0) delta -= 1L << (8 * offBytes); // valor com sinal
            p += offBytes;
            if (offBytes == 0) continue; // trecho esparso: não existe na MFT
            lcn += delta;
            runs.Add((lcn, length));
        }
        return runs;
    }

    private static DateTime FromFileTime(long ft)
    {
        try { return ft > 0 ? DateTime.FromFileTimeUtc(ft) : DateTime.MinValue; }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }

    private static int Align(int value, int to) => (value + to - 1) / to * to;

    // =====================================================================
    // Acesso ao volume
    // =====================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct NtfsVolumeData
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize,
        out NtfsVolumeData outBuffer, int outSize, out int returned, IntPtr overlapped);

    private static SafeFileHandle OpenVolume(string root)
    {
        const uint GenericRead = 0x80000000;
        const uint ShareAll = 0x1 | 0x2 | 0x4;
        const uint OpenExisting = 3;
        var h = CreateFile(@"\\.\" + root.TrimEnd('\\'), GenericRead, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new UnauthorizedAccessException("Sem acesso direto ao volume (é preciso administrador).");
        return h;
    }

    private static NtfsVolumeData GetVolumeData(SafeFileHandle volume)
    {
        const uint FsctlGetNtfsVolumeData = 0x00090064;
        if (!DeviceIoControl(volume, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, out var data, Marshal.SizeOf<NtfsVolumeData>(), out _, IntPtr.Zero))
            throw new NotSupportedException("O volume não é NTFS.");
        return data;
    }
}
