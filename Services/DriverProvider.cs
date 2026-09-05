using System;
using System.Threading;
using System.Threading.Tasks;
using KaliteKit.Models;

namespace KaliteKit.Services
{
    /// <summary>
    /// A vendor backend that knows how to find the latest driver for a GPU.
    /// Providers are small and focused: detect whether they own a GPU, then
    /// query their vendor's source and return a <see cref="DriverInfo"/>.
    /// </summary>
    public interface IDriverProvider
    {
        /// <summary>Which vendor this provider services (NVIDIA / AMD / Intel).</summary>
        string Vendor { get; }

        /// <summary>Whether this provider should handle the given GPU.</summary>
        bool CanHandle(GpuInfo gpu);

        /// <summary>
        /// Returns the latest driver for the GPU, or null when none could be
        /// determined. Throwing is allowed — <see cref="DriverService"/> turns
        /// unexpected exceptions into a status result.
        /// </summary>
        Task<DriverInfo?> GetLatestDriverAsync(GpuInfo gpu, CancellationToken cancellationToken = default);
    }
}