using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>Helpers for asserting on <see cref="RoofControllerException"/> failures.</summary>
internal static class ResultExtensions
{
    /// <summary>The <see cref="RoofControllerErrorCode"/> of a failed result, or null when it succeeded or failed otherwise.</summary>
    public static RoofControllerErrorCode? ErrorCode<T>(this Result<T> result)
        => !result.IsSuccessful && result.Error is RoofControllerException ex ? ex.Code : null;
}
