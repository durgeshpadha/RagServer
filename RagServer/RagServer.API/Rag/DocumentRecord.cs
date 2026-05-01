public class DocumentRecord
{
    public string Id { get; set; } = "";
    // Original source file path
    public string Source { get; set; } = "";
    // Chunk index within the source document (0-based)
    public int ChunkIndex { get; set; } = 0;
    public string Text { get; set; } = "";
    public float[] Embedding { get; set; } = Array.Empty<float>();
}
