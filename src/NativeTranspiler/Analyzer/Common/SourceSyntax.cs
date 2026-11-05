using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace NativeTranspiler.Analyzer.Common
{
    // 改写节点的来源跨度属于调用者持有的 SemanticModel；查询始终回到该原始语法树。
    internal static class SourceSyntax
    {
        private const string Origin = "EntJoy.NativeTranspiler.Source";

        public static T Annotate<T>(T node) where T : SyntaxNode
            => (T)new OriginRewriter().Visit(node)!;

        public static T WithOrigin<T>(T node, SyntaxNode source) where T : SyntaxNode
        {
            var annotation = source.GetAnnotations(Origin).FirstOrDefault()
                ?? new SyntaxAnnotation(Origin, string.Format(CultureInfo.InvariantCulture,
                    "{0}:{1}:{2}", source.SpanStart, source.Span.Length, source.RawKind));
            return node.WithoutAnnotations(Origin).WithAdditionalAnnotations(annotation);
        }

        public static SyntaxNode? Original(SemanticModel model, SyntaxNode node)
        {
            if (node.SyntaxTree == model.SyntaxTree) return node;
            var data = node.GetAnnotations(Origin).FirstOrDefault()?.Data?.Split(':');
            if (data?.Length != 3 || !int.TryParse(data[0], out int start)
                || !int.TryParse(data[1], out int length) || !int.TryParse(data[2], out int kind)) return null;
            var root = model.SyntaxTree.GetRoot();
            var span = new TextSpan(start, length);
            if (!root.FullSpan.Contains(span)) return null;
            for (var source = root.FindNode(span, getInnermostNodeForTie: true);
                source != null && source.Span == span; source = source.Parent)
                if (source.RawKind == kind) return source;
            return null;
        }

        public static Optional<object?> Constant(SemanticModel model, SyntaxNode node)
            => Original(model, node) is SyntaxNode source ? model.GetConstantValue(source) : default;

        private sealed class OriginRewriter : CSharpSyntaxRewriter
        {
            public override SyntaxNode? Visit(SyntaxNode? node)
                => node == null ? null : WithOrigin(base.Visit(node)!, node);
        }
    }
}
