using System;
using System.Text;

namespace EntJoy.SourceGenerator.Utils
{
    /// <summary>
    /// 代码生成器
    /// </summary>
    internal sealed class CodeWriter
    {
        readonly StringBuilder _builder = new StringBuilder();
        private int _indentLevel = 0;

        public int GetindentLevel { get => _indentLevel; set; }

        public void AppendLine(string value = "")
        {
            if (string.IsNullOrEmpty(value))
            {
                _builder.AppendLine();
            }
            else
            {
                if (value.Contains("\n"))
                {
                    foreach (var item in value.Split('\n'))
                    {
                        _builder.Append($"{new string(' ', _indentLevel * 4)}{item}\n");
                    }
                }
                else
                {
                    _builder.AppendLine($"{new string(' ', _indentLevel * 4)}{value}");
                }
                
                
            }
        }


        //增加缩进级别
        public void IncreaseIndent()
        {
            _indentLevel+=1;
        }

        //减少缩进级别
        public void DecreaseIndent()
        {
             _indentLevel-=1;
        }
        // 开始一个新的代码块
        public void BeginBlock()
        {
            AppendLine("{");
            IncreaseIndent();
        }
        // 结束一个代码块,并可以选择是否加上分号
        public void EndBlock(bool withSemicolon = false)
        {
            DecreaseIndent();
            AppendLine(withSemicolon ? "};" : "}");
        }

        public void Clear()
        {
            _builder.Clear();
        }

        public override string ToString()
        {
            return _builder.ToString();
        }

        private readonly struct IndentScope : IDisposable
        {
            private readonly CodeWriter _writer;

            public IndentScope(CodeWriter writer)
            {
                _writer = writer;
                _writer.IncreaseIndent();
            }

            public void Dispose()
            {
                _writer.DecreaseIndent();
            }
        }

        //缩进
        public IDisposable BeginIndentScope()
        {
            return new IndentScope(this);
        }

        private readonly struct BlockScope : IDisposable
        {
            private readonly CodeWriter _writer;
            private readonly string _startLine;
            private readonly bool _withSemicolon;

            public BlockScope(CodeWriter writer,string startLine = null, bool withSemicolon = false)
            {
                _writer = writer;
                _withSemicolon = withSemicolon;
                _startLine = startLine;
                _writer.AppendLine(_startLine);
                _writer.BeginBlock();
            }

            public void Dispose()
            {
                _writer.EndBlock();
            }
        }

        public IDisposable BeginBlockScope(string strand = null, bool withSemicolon = false)
        {
            return new BlockScope(this, strand, withSemicolon);
        }
    }
}
