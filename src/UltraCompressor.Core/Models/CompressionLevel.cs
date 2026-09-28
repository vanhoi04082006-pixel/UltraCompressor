namespace UltraCompressor.Core.Models;

/// <summary>
/// Mức nén. Giữ nguyên thứ tự và ý nghĩa như bản gốc v12:
/// Light = chất lượng cao / size lớn nhất, Strong = nhỏ nhất.
/// </summary>
public enum CompressionLevel
{
    Light = 0,
    Balanced = 1,
    Strong = 2,
}
