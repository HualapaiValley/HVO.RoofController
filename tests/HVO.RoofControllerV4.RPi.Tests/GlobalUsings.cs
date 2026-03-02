// Global test assembly configuration for HVO.RoofControllerV4.RPi.Tests

using Microsoft.VisualStudio.TestTools.UnitTesting;

// Enable test parallelization at method level for better performance
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]
