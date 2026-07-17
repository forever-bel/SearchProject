using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SearchProject.API.Services;

public class EmbedderService : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _dimension;
    private readonly ILogger<EmbedderService> _logger;
    private readonly string _outputName;

    public EmbedderService(IConfiguration configuration, ILogger<EmbedderService> logger)
    {
        _logger = logger;
        var modelPath = configuration["EmbedderSettings:ModelPath"]
            ?? throw new Exception("Ошибка конфигурации");

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Модель не найдена: {modelPath}");
        }

        var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;

        _session = new InferenceSession(modelPath, options);

        _logger.LogInformation($"Модель загружена: {modelPath}");
        _logger.LogInformation($"Входы: {string.Join(", ", _session.InputMetadata.Keys)}");
        _logger.LogInformation($"Выходы: {string.Join(", ", _session.OutputMetadata.Keys)}");

        _outputName = DetermineOutputName();
        _dimension = DetermineDimension();

        _logger.LogInformation($"Выходной слой: {_outputName}");
        _logger.LogInformation($"Размерность вектора: {_dimension}");
    }

    public int Dimension => _dimension;

    public float[] EmbedText(string text)
    {
        return EmbedBatch(new[] { text })[0];
    }

    public float[][] EmbedBatch(string[] texts)
    {
        try
        {
            var (inputIds, attentionMask) = Tokenize(texts);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
            };

            var results = _session.Run(inputs);
            var output = results.First(x => x.Name == _outputName);
            var outputTensor = output.AsTensor<float>();

            var flatArray = outputTensor.ToArray();
            var batchSize = texts.Length;

            var dims = outputTensor.Dimensions;
            int dim;
            if (dims.Length >= 2)
                dim = (int)dims[1];
            else
                dim = flatArray.Length / batchSize;

            var result = new float[batchSize][];
            for (int i = 0; i < batchSize; i++)
            {
                result[i] = new float[dim];
                Array.Copy(flatArray, i * dim, result[i], 0, dim);
            }

            return NormalizeEmbeddings(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при создании эмбеддингов");
            throw;
        }
    }

    private string DetermineOutputName()
    {
        var outputs = _session.OutputMetadata.Keys.ToList();

        if (outputs.Contains("sentence_embedding"))
            return "sentence_embedding";
        if (outputs.Contains("pooler_output"))
            return "pooler_output";
        if (outputs.Contains("last_hidden_state"))
            return "last_hidden_state";
        if (outputs.Contains("output"))
            return "output";

        return outputs.FirstOrDefault() ?? throw new Exception("Выходной слой не найден");
    }

    private int DetermineDimension()
    {
        var metadata = _session.OutputMetadata[_outputName];
        var dims = metadata.Dimensions;

        if (dims.Length >= 2 && dims[1] > 0)
            return (int)dims[1];
        if (dims.Length >= 1 && dims[0] > 0)
            return (int)dims[0];

        return 512;
    }

    private float[][] NormalizeEmbeddings(float[][] embeddings)
    {
        foreach (var vec in embeddings)
        {
            var norm = Math.Sqrt(vec.Sum(x => x * x));
            if (norm > 0.0001)
            {
                for (int j = 0; j < vec.Length; j++)
                    vec[j] /= (float)norm;
            }
        }
        return embeddings;
    }

    private (DenseTensor<long> InputIds, DenseTensor<long> AttentionMask)
        Tokenize(string[] texts)
    {
        var maxLength = 128;
        var batchSize = texts.Length;

        var inputIds = new DenseTensor<long>(new[] { batchSize, maxLength });
        var attentionMask = new DenseTensor<long>(new[] { batchSize, maxLength });

        for (int i = 0; i < batchSize; i++)
        {
            var tokens = texts[i].Split(' ').Take(maxLength - 2);
            int j = 0;

            inputIds[i, j] = 101;
            attentionMask[i, j] = 1;
            j++;

            foreach (var token in tokens)
            {
                inputIds[i, j] = Math.Abs(token.GetHashCode()) % 30000 + 100;
                attentionMask[i, j] = 1;
                j++;
            }

            if (j < maxLength)
            {
                inputIds[i, j] = 102;
                attentionMask[i, j] = 1;
            }
        }

        return (inputIds, attentionMask);
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}