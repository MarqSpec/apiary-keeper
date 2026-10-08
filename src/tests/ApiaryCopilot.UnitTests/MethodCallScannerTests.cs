using System.Linq.Expressions;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.UnitTests;

// Red controls for the IgnoreQueryFilters guard (CONVENTIONS.md §8.4): the scan must hear each way
// code can reach the method, or its clean result in ApiaryCopilotDbContextTests proves nothing.
public sealed class MethodCallScannerTests
{
    private const string Target = nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters);

    [Fact]
    public void FindCallsTo_ShouldReportTheMethod_WhenItCallsTheTargetDirectly()
    {
        var scan = MethodCallScanner.FindCallsTo(Target, [typeof(MethodCallScannerTests).Assembly]);

        scan.Callers.Should().Contain(c => c.EndsWith($"{nameof(BypassFixture)}.{nameof(BypassFixture.Direct)}", StringComparison.Ordinal));
    }

    [Fact]
    public void FindCallsTo_ShouldReportTheStateMachine_WhenAnAsyncMethodCallsTheTarget()
    {
        var scan = MethodCallScanner.FindCallsTo(Target, [typeof(MethodCallScannerTests).Assembly]);

        scan.Callers.Should().Contain(c => c.Contains($"<{nameof(BypassFixture.InAsyncMethod)}>", StringComparison.Ordinal));
    }

    [Fact]
    public void FindCallsTo_ShouldReportTheMethod_WhenAnExpressionTreeReferencesTheTarget()
    {
        var scan = MethodCallScanner.FindCallsTo(Target, [typeof(MethodCallScannerTests).Assembly]);

        scan.Callers.Should().Contain(c => c.EndsWith($"{nameof(BypassFixture)}.{nameof(BypassFixture.InExpressionTree)}", StringComparison.Ordinal));
    }

    [Fact]
    public void FindCallsTo_ShouldReportNothing_WhenNoMethodReferencesTheTarget()
    {
        var scan = MethodCallScanner.FindCallsTo(Target, [typeof(AccountId).Assembly]);

        scan.Callers.Should().BeEmpty();
    }

    private static class BypassFixture
    {
        public static IQueryable<Account> Direct(ApiaryCopilotDbContext db) => db.Accounts.IgnoreQueryFilters();

        public static async Task<int> InAsyncMethod(ApiaryCopilotDbContext db) =>
            await db.Accounts.IgnoreQueryFilters().CountAsync();

        public static Expression<Func<ApiaryCopilotDbContext, int>> InExpressionTree() =>
            db => db.Accounts.IgnoreQueryFilters().Count();
    }
}
