using System;
using Bezoro.Core.CodeGen;
using Xunit;

namespace Bezoro.Core.Tests.CodeGen;

public class CodeWriterTests
{
	[Fact]
	public void WritingALineAfterTextDoesNotInsertAnotherIndent()
	{
		var writer = new CodeWriter();
		using (writer.BeginScope()) writer.Write("left").WriteLine("right");
		Assert.Contains("    leftright" + Environment.NewLine, writer.ToString());
	}

	[Fact]
	public void ScopeDisposalIsIdempotentAndUnbalancedCloseIsRejected()
	{
		var writer = new CodeWriter();
		var scope = writer.BeginScope();
		scope.Dispose();
		scope.Dispose();
		Assert.Throws<InvalidOperationException>(() => writer.EndScope());
	}

	[Fact]
	public void ScopesMustCloseInNestingOrder()
	{
		var writer = new CodeWriter();
		var outer = writer.BeginScope();
		var inner = writer.BeginScope();
		Assert.Throws<InvalidOperationException>(() => outer.Dispose());
		inner.Dispose();
		outer.Dispose();
	}

	[Fact]
	public void NamespaceCannotCrossAClassBoundary()
	{
		var builder = new CSharpCodeBuilder().BeginNamespace("Example").BeginClass("Actor");
		Assert.Throws<InvalidOperationException>(() => builder.BeginNamespace("Invalid"));
		Assert.Throws<InvalidOperationException>(() => builder.EndNamespace());
		Assert.Contains("class Actor", builder.EndClass().EndNamespace().Generate());
	}
}
