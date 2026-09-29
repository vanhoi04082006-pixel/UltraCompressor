using System.Text;

// Kiem tra chu tieng Viet co bi "giai ma sai" trong file nguon.
//
// Nguyen nhan lan truoc: mot so script PowerShell doc file bang Get-Content. Windows
// PowerShell 5.1 mac dinh doc lai bang ANSI/CP1252, nen mot chuoi UTF-8 doc sai se bi
// giai ma sai mot lan roi ghi nguoc lai: "chay" thanh "chaÌ¡y", "nen" thanh "nĂªn".
// 65 dong trong MediaHost.cs va AppHost.cs da bi hong theo cach nay va duoc commit.
//
// Buoc nay chan sanh ra. Dau hieu: nhung ky tu thuoc ve nhom "moi byte thanh mot ky tu
// khi giai ma sai UTF-8 thanh bang ma trang" — trong ma nguon tieng Viet co dau, chung
// khong xuat hien.
//
// KHONG dua dau cham · (U+00B7) va § (U+00A7) vao danh sach: thu muc tep, dau gach
// long va ky hieu muc (§11) deu dung chung, dua vao se bao dong gia.

var tellTale = new[] { 'º', '¡', '´', '¬', '»', '«', '¨',
                        '½', '¼', '¾', '¹', '²', '³', '¢',
                        '£', '¥', '¿', '°', '¶',
                        '¦', 'ª' };

var strict = new UTF8Encoding(false, true);
var roots = args.Length > 0 ? args : ["src", "tests", "docs"];
var extensions = new[] { ".cs", ".js", ".html", ".ps1", ".md" };

var totalFiles = 0;
var totalLines = 0;

foreach (var root in roots)
{
    if (!Directory.Exists(root)) continue;

    foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        var ext = Path.GetExtension(path);
        if (!extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

        // Thu muc ket qua bien dich: khong phai ma nguon.
        if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            continue;
        }

        totalFiles++;

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];

        string text;
        try
        {
            text = strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Console.WriteLine($"KHONG PHAI UTF-8 HOP LE: {path}");
            totalLines++;
            continue;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Any(tellTale.Contains)) continue;

            Console.WriteLine($"{path}:{i + 1}");
            Console.WriteLine($"    {lines[i].Trim()}");
            totalLines++;
        }
    }
}

Console.WriteLine();
Console.WriteLine($"Kiem tra {totalFiles} tep, tim thay {totalLines} dong bi hong.");

if (totalLines > 0)
{
    Console.WriteLine();
    Console.WriteLine("Sua dung cach doc/ghi: dung [System.IO.File]::ReadAllLines / WriteAllLines");
    Console.WriteLine("voi [System.Text.UTF8Encoding]::new($false). KHONG dung Get-Content/Set-Content.");
    Console.WriteLine("Nen dung cong cu edit de sua file truc tiep.");
    Environment.Exit(1);
}

Environment.Exit(0);
