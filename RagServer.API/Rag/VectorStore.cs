using System.Text.Json;
using Microsoft.Extensions.Options;

public class VectorStore
{
    private readonly string _path;
    private List<DocumentRecord> _docs = new();
    private readonly Lock _lock = new();

    public VectorStore(IHostEnvironment env, IOptions<RagOptions> options, ILogger<VectorStore> logger)
    {
        var configuredPath = options.Value.VectorStorePath;
        _path = Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, configuredPath));

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<List<DocumentRecord>>(json);
            if (loaded != null)
            {
                _docs = loaded;
            }
        }
        catch (JsonException ex)
        {
            var backupPath = _path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Copy(_path, backupPath, overwrite: true);
            }
            catch
            {
                // Best effort backup only.
            }

            _docs = new List<DocumentRecord>();
            logger.LogWarning(ex, "Vector store file is malformed. Starting with empty in-memory store. Path: {Path}", _path);
        }
    }

    public IReadOnlyList<DocumentRecord> Documents
    {
        get
        {
            lock (_lock)
            {
                return _docs.ToArray();
            }
        }
    }

    public void Add(DocumentRecord doc)
    {
        lock (_lock)
        {
            _docs.Add(doc);
        }
    }

    public void AddRange(IEnumerable<DocumentRecord> docs)
    {
        lock (_lock)
        {
            _docs.AddRange(docs);
        }
    }

    public int RemoveBySource(string source)
    {
        lock (_lock)
        {
            return _docs.RemoveAll(d => string.Equals(d.Source, source, StringComparison.OrdinalIgnoreCase));
        }
    }

    public int Clear()
    {
        lock (_lock)
        {
            var count = _docs.Count;
            _docs.Clear();
            return count;
        }
    }

    public void Save()
    {
        List<DocumentRecord> snapshot;
        lock (_lock)
        {
            snapshot = _docs.ToList();
        }

        var json = JsonSerializer.Serialize(snapshot);
        File.WriteAllText(_path, json);
    }

    public List<DocumentRecord> Query(float[] queryEmbedding, int topK)
    {
        List<DocumentRecord> snapshot;
        lock (_lock)
        {
            snapshot = _docs.ToList();
        }

        return snapshot
            .Select(d => new
            {
                Doc = d,
                Score = CosineSimilarity(queryEmbedding, d.Embedding)
            })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Doc)
            .ToList();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return -1;

        double dot = 0;
        double na = 0;
        double nb = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        if (na == 0 || nb == 0) return 0;
        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}
