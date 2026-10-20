using ImageTo3D.Inference.Configuration;
using Microsoft.ML.OnnxRuntime;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace ImageTo3D.Inference.Models;

public static class OnnxSessionFactory
{
    public static InferenceSession Create(string path, InferenceOptions options, ILogger logger)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"ONNX model not found: {Path.GetFileName(path)}. See models/README.md for download instructions.", path);

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = options.EffectiveIntraOpThreads,
        };

        if (string.Equals(options.ExecutionProvider, "Cuda", StringComparison.OrdinalIgnoreCase))
        {
            // Throws if the native ORT build has no CUDA provider (CPU package) or CUDA libraries are missing.
            sessionOptions.AppendExecutionProvider_CUDA(options.CudaDeviceId);
        }

        var session = new InferenceSession(path, sessionOptions);
        foreach (var (name, meta) in session.InputMetadata)
            logger.LogInformation("Model {Model} input {Name}: {Type} [{Shape}]", Path.GetFileName(path), name, meta.ElementDataType, string.Join(",", meta.Dimensions));
        foreach (var (name, meta) in session.OutputMetadata)
            logger.LogInformation("Model {Model} output {Name}: {Type} [{Shape}]", Path.GetFileName(path), name, meta.ElementDataType, string.Join(",", meta.Dimensions));
        return session;
    }

    /// <summary>Wires a cancellation token to ORT's cooperative termination flag for the duration of a Run call.</summary>
    public static CancellationTokenRegistration LinkTermination(RunOptions runOptions, CancellationToken cancellationToken) =>
        cancellationToken.Register(() => runOptions.Terminate = true);
}
