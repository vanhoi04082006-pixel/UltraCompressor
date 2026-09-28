using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Scheduling;

public sealed record ScanResult(Job Job, IReadOnlyList<string> Errors);

/// <summary>Quét thư mục, lập danh sách tệp cần nén theo bộ lọc cấu hình.</summary>
public sealed class FolderScanner(AppConfig config)
{
    private readonly AppConfig _config = config;

    public ScanResult Scan(string folderPath, CompressionLevel level, bool dryRun, string? outputFolder)
    {
        var errors = new List<string>();
        var job = new Job
        {
            FolderPath = Path.GetFullPath(folderPath),
            Level = level,
            DryRun = dryRun,
            OutputFolder = outputFolder,
        };

        if (!Directory.Exists(job.FolderPath))
        {
            errors.Add($"Không tìm thấy thư mục '{job.FolderPath}'.");
            return new ScanResult(job, errors);
        }

        var search = _config.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string[] found;

        try
        {
            found = Directory.GetFiles(job.FolderPath, "*", search);
        }
        catch (Exception ex)
        {
            errors.Add($"Không quét được thư mục: {ex.Message}");
            return new ScanResult(job, errors);
        }

        foreach (var file in found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            var kind = MediaClassifier.Classify(file);

            // Bản gốc không lọc gì cả, nên chạy lần hai là nén tiếp chính tệp .bak do
            // lần trước tạo ra. Bộ lọc mặc định chặn *.bak trước khi xét định dạng.
            if (IsExcluded(name))
            {
                job.Items.Add(Mark(file, kind, SkipReason.ExcludedByFilter, 0));
                continue;
            }

            if (kind == MediaKind.Unknown)
            {
                job.Items.Add(Mark(file, kind, SkipReason.UnsupportedFormat, 0));
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch
            {
                job.Items.Add(Mark(file, kind, SkipReason.Error, 0, "Không đọc được kích thước."));
                continue;
            }

            if (_config.MinFileSizeBytes > 0 && size < _config.MinFileSizeBytes)
            {
                job.Items.Add(Mark(file, kind, SkipReason.ExcludedByFilter, size,
                    $"Nhỏ hơn ngưỡng {Format.Size(_config.MinFileSizeBytes)}."));
                continue;
            }

            job.Items.Add(new JobItem { FilePath = file, Kind = kind, OldSize = size });
        }

        // Không có tệp nào cần xử lý thì báo rõ thay vì tạo job rỗng rồi chạy không ra gì.
        if (job.Items.All(i => i.IsComplete))
        {
            errors.Add("Không có tệp phù hợp để nén trong thư mục này.");
        }

        return new ScanResult(job, errors);
    }

    private bool IsExcluded(string fileName)
    {
        foreach (var pattern in _config.ExcludePatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            if (MatchesWildcard(fileName, pattern)) return true;
        }

        return false;
    }

    /// <summary>
    /// So khớp mẫu tên tệp kiểu ký tự đại diện. Hỗ trợ <c>*</c> và <c>?</c>, không phân biệt
    /// hoa thường. Tự viết thay vì dùng <c>Path.MatchesSimpleExpression</c> vì bản đó chỉ có
    /// ở một số phiên bản .NET và cũng không tôn trọng quy ước của Explorer.
    /// </summary>
    public static bool MatchesWildcard(string fileName, string pattern)
    {
        var name = fileName.AsSpan();
        var pat = pattern.AsSpan();
        int n = 0, p = 0, star = -1, mark = 0;

        while (n < name.Length)
        {
            if (p < pat.Length && (pat[p] == '?' || char.ToUpperInvariant(pat[p]) == char.ToUpperInvariant(name[n])))
            {
                n++;
                p++;
            }
            else if (p < pat.Length && pat[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pat.Length && pat[p] == '*') p++;

        return p == pat.Length;
    }

    private static JobItem Mark(string file, MediaKind kind, SkipReason reason, long size, string? message = null) => new()
    {
        FilePath = file,
        Kind = kind,
        OldSize = size,
        NewSize = size,
        IsComplete = true,
        Skip = reason,
        Message = message,
    };
}
