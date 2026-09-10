using Microsoft.ML.OnnxRuntime;

namespace IrisTrackAI.Services;

public static class OnnxSessionFactory
{
    public static InferenceSession Create(string path, out string provider, bool cpuOnly = false)
    {
        if (!cpuOnly)
        {
            try
            {
                using var gpu = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    EnableMemoryPattern = false
                };
                gpu.AppendExecutionProvider_DML(0);
                var session = new InferenceSession(path, gpu);
                provider = "DirectML";
                return session;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
            {
                // Equipos sin DirectML también pueden usar el módulo, en CPU.
            }
        }
        using var cpu = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1
        };
        cpu.AppendExecutionProvider_CPU();
        var fallback = new InferenceSession(path, cpu);
        provider = "CPU";
        return fallback;
    }
}
