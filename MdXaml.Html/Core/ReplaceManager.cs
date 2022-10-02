﻿using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Windows.Documents;
using Paragraph = System.Windows.Documents.Paragraph;
using System.Windows.Input;
using MdXaml.Html.Core.Parsers;
using MdXaml.Html.Core.Utils;
using MdXaml.Html.Core.Parsers.MarkdigExtensions;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Linq;
using System.Text;
using MdXaml;
using MdXaml.Plugins;

namespace MdXaml.Html.Core
{
    public class ReplaceManager
    {
        private readonly Dictionary<string, List<IInlineTagParser>> _inlineBindParsers;
        private readonly Dictionary<string, List<IBlockTagParser>> _blockBindParsers;
        private readonly Dictionary<string, List<ITagParser>> _bindParsers;

        private TextNodeParser textParser;

        public ReplaceManager()
        {
            _inlineBindParsers = new();
            _blockBindParsers = new();
            _bindParsers = new();

            UnknownTags = UnknownTagsOption.Drop;

            Register(new TagIgnoreParser());
            Register(new CommentParsre());
            Register(new ImageParser());
            Register(new CodeBlockParser());
            //Register(new CodeSpanParser());
            Register(new OrderListParser());
            Register(new UnorderListParser());
            Register(textParser = new TextNodeParser());
            Register(new HorizontalRuleParser());
            Register(new FigureParser());
            Register(new GridTableParser());
            Register(new InputParser());
            Register(new ButtonParser());
            Register(new TextAreaParser());
            Register(new ProgressParser());

            foreach (var parser in TypicalBlockParser.Load())
                Register(parser);

            foreach (var parser in TypicalInlineParser.Load())
                Register(parser);
        }

        public IEnumerable<string> InlineTags => _inlineBindParsers.Keys.Where(tag => !tag.StartsWith("#"));
        public IEnumerable<string> BlockTags => _blockBindParsers.Keys.Where(tag => !tag.StartsWith("#"));

        public bool MaybeSupportBodyTag(string tagName)
            => _blockBindParsers.ContainsKey(tagName.ToLower());

        public bool MaybeSupportInlineTag(string tagName)
            => _inlineBindParsers.ContainsKey(tagName.ToLower());

        public UnknownTagsOption UnknownTags { get; set; }

        public IMarkdown Engine { get; set; }

        public ICommand? HyperlinkCommand => Engine.HyperlinkCommand;

        public Uri? BaseUri => Engine.BaseUri;

        public string? AssetPathRoot => Engine.AssetPathRoot;

        public void Register(ITagParser parser)
        {

            if (parser is IInlineTagParser inlineParser)
            {
                PrivateRegister(inlineParser, _inlineBindParsers);
            }
            if (parser is IBlockTagParser blockParser)
            {
                PrivateRegister(blockParser, _blockBindParsers);
            }

            PrivateRegister(parser, _bindParsers);

            static void PrivateRegister<T>(T parser, Dictionary<string, List<T>> bindParsers) where T : ITagParser
            {
                foreach (var tag in parser.SupportTag)
                {
                    if (!bindParsers.TryGetValue(tag.ToLower(), out var list))
                    {
                        list = new();
                        bindParsers.Add(tag.ToLower(), list);
                    }

                    int parserPriority = GetPriority(parser);

                    int i = 0;
                    int count = list.Count;
                    for (; i < count; ++i)
                        if (parserPriority <= GetPriority(list[i]))
                            break;

                    list.Insert(i, parser);
                }
            }

            static int GetPriority(object? p)
                => p is IHasPriority prop ? prop.Priority : HasPriority.DefaultPriority;
        }

        public string GetTag(Tags tag)
        {
            return tag.ToString().Substring(3);
        }

        /// <summary>
        /// Convert a html tag list to an element of markdown.
        /// </summary>
        public IEnumerable<Block> Parse(string htmldoc)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(htmldoc);

            return Parse(doc);
        }

        /// <summary>
        /// Convert a html tag list to an element of markdown.
        /// </summary>
        public IEnumerable<Block> Parse(HtmlDocument doc)
        {
            var contents = new List<HtmlNode>();

            var head = PickBodyOrHead(doc.DocumentNode, "head");
            if (head is not null)
                contents.AddRange(head.ChildNodes.SkipComment());

            var body = PickBodyOrHead(doc.DocumentNode, "body");
            if (body is not null)
                contents.AddRange(body.ChildNodes.SkipComment());

            if (contents.Count == 0)
            {
                var root = doc.DocumentNode.ChildNodes.SkipComment();

                if (root.Count == 1 && string.Equals(root[0].Name, "html", StringComparison.OrdinalIgnoreCase))
                    contents.AddRange(root[0].ChildNodes.SkipComment());
                else
                    contents.AddRange(root);
            }

            var jaggingResult = ParseJagging(contents);

            return Grouping(jaggingResult);
        }

        /// <summary>
        /// Convert html tag children to an element of markdown.
        /// Inline elements are aggreated into paragraph.
        /// </summary>
        public IEnumerable<Block> ParseChildrenAndGroup(HtmlNode node)
        {
            var jaggingResult = ParseChildrenJagging(node);

            return Grouping(jaggingResult);
        }

        /// <summary>
        /// Convert html tag children to an element of markdown.
        /// this result contains a block element and an inline element.
        /// </summary>
        public IEnumerable<TextElement> ParseChildrenJagging(HtmlNode node)
        {
            // search empty line
            var empNd = node.ChildNodes
                            .Select((nd, idx) => new { Node = nd, Index = idx })
                            .Where(tpl => tpl.Node is HtmlTextNode)
                            .Select(tpl => new
                            {
                                NodeIndex = tpl.Index,
                                TextIndex = tpl.Node.InnerText.IndexOf("\n\n")
                            })
                            .FirstOrDefault(tpl => tpl.TextIndex != -1);


            if (empNd is null)
            {
                return ParseJagging(node.ChildNodes);
            }
            else
            {
                return ParseJaggingAndRunBlockGamut(node.ChildNodes, empNd.NodeIndex, empNd.TextIndex);
            }
        }

        /// <summary>
        /// Convert a html tag to an element of markdown.
        /// this result contains a block element and an inline element.
        /// </summary>
        private IEnumerable<TextElement> ParseJagging(IEnumerable<HtmlNode> nodes)
        {
            bool isPrevBlock = true;
            TextElement? lastElement = null;

            foreach (var node in nodes)
            {
                if (node.IsComment())
                    continue;

                // remove blank text between the blocks.
                if (isPrevBlock
                    && node is HtmlTextNode txt
                    && String.IsNullOrWhiteSpace(txt.Text))
                    continue;

                foreach (var element in ParseBlockAndInline(node))
                {
                    lastElement = element;
                    yield return element;
                }

                isPrevBlock = lastElement is Block;
            }
        }

        private IEnumerable<TextElement> ParseJaggingAndRunBlockGamut(IEnumerable<HtmlNode> nodes, int nodeIdx, int textIdx)
        {
            var parseTargets = new List<HtmlNode>();
            var textBuf = new StringBuilder();
            var mdTextBuf = new StringBuilder();

            foreach (var tpl in nodes.Select((value, i) => new { Node = value, Index = i }))
            {
                if (tpl.Index < nodeIdx)
                {
                    parseTargets.Add(tpl.Node);
                }
                else if (tpl.Index == nodeIdx)
                {
                    var nodeText = tpl.Node.InnerText;

                    textBuf.Append(nodeText.Substring(0, textIdx));
                    mdTextBuf.Append(nodeText.Substring(textIdx + 2));
                }
                else
                {
                    mdTextBuf.Append(tpl.Node.OuterHtml);
                }
            }

            foreach (var elm in ParseJagging(parseTargets))
                yield return elm;

            foreach (var elm in textParser.Replace(textBuf.ToString(), this))
                yield return elm;

            foreach (var elm in Engine.RunBlockGamut(mdTextBuf.ToString(), true))
                yield return elm;
        }

        /// <summary>
        /// Convert a html tag to an element of markdown.
        /// Only tag node and text node are accepted.
        /// </summary>
        /// <param name="node"></param>
        /// <returns></returns>
        public IEnumerable<TextElement> ParseBlockAndInline(HtmlNode node)
        {
            if (_bindParsers.TryGetValue(node.Name.ToLower(), out var binds))
            {
                foreach (var bind in binds)
                {
                    if (bind.TryReplace(node, this, out var parsed))
                    {
                        return parsed;
                    }
                }
            }

            return UnknownTags switch
            {
                UnknownTagsOption.PassThrough
                    => HtmlUtils.IsBlockTag(node.Name) ?
                        new[] { new Paragraph(new Run() { Text = node.OuterHtml }) } :
                        new[] { new Run(node.OuterHtml) },

                UnknownTagsOption.Drop
                    => EnumerableExt.Empty<TextElement>(),

                UnknownTagsOption.Bypass
                    => ParseJagging(node.ChildNodes),

                _ => throw new UnknownTagException(node)
            };
        }

        public IEnumerable<Block> ParseBlock(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            foreach (var node in doc.DocumentNode.ChildNodes)
                foreach (var block in ParseBlock(node))
                    yield return block;
        }

        public IEnumerable<Inline> ParseInline(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            foreach (var node in doc.DocumentNode.ChildNodes)
                foreach (var inline in ParseInline(node))
                    yield return inline;
        }

        public IEnumerable<Block> ParseBlock(HtmlNode node)
        {
            if (_blockBindParsers.TryGetValue(node.Name.ToLower(), out var binds))
            {
                foreach (var bind in binds)
                {
                    if (bind.TryReplace(node, this, out var parsed))
                    {
                        return parsed;
                    }
                }
            }

            return UnknownTags switch
            {
                UnknownTagsOption.PassThrough
                    => new[] {
                        new Paragraph(
                            HtmlUtils.IsBlockTag(node.Name) ?
                                new Run() { Text = node.OuterHtml }:
                                new Run(node.OuterHtml)
                        )
                    },

                UnknownTagsOption.Drop
                    => EnumerableExt.Empty<Block>(),

                UnknownTagsOption.Bypass
                    => node.ChildNodes
                           .SkipComment()
                           .SelectMany(nd => ParseBlock(nd)),

                _ => throw new UnknownTagException(node)
            };
        }

        public IEnumerable<Inline> ParseInline(HtmlNode node)
        {
            if (_inlineBindParsers.TryGetValue(node.Name.ToLower(), out var binds))
            {
                foreach (var bind in binds)
                {
                    if (bind.TryReplace(node, this, out var parsed))
                    {
                        return parsed;
                    }
                }
            }

            return UnknownTags switch
            {
                UnknownTagsOption.PassThrough
                    => HtmlUtils.IsBlockTag(node.Name) ?
                        new[] { new Run() { Text = node.OuterHtml } } :
                        new[] { new Run(node.OuterHtml) },

                UnknownTagsOption.Drop
                    => EnumerableExt.Empty<Inline>(),

                UnknownTagsOption.Bypass
                    => node.ChildNodes
                           .SkipComment()
                           .SelectMany(nd => ParseInline(nd)),

                _ => throw new UnknownTagException(node)
            };
        }

        /// <summary>
        /// Convert IMdElement to IMdBlock.
        /// Inline elements are aggreated into paragraph.
        /// </summary>
        public IEnumerable<Block> Grouping(IEnumerable<TextElement> elements)
        {
            static Paragraph? Group(IList<Inline> inlines)
            {
                // trim whiltepace plain

                while (inlines.Count > 0)
                {
                    if (inlines[0] is Run run
                        && String.IsNullOrWhiteSpace(run.Text))
                    {
                        inlines.RemoveAt(0);
                    }
                    else break;
                }

                while (inlines.Count > 0)
                {
                    if (inlines[inlines.Count - 1] is Run run
                        && String.IsNullOrWhiteSpace(run.Text))
                    {
                        inlines.RemoveAt(inlines.Count - 1);
                    }
                    else break;
                }

                using (var list = inlines.GetEnumerator())
                {
                    Inline? prev = null;

                    if (list.MoveNext())
                    {
                        prev = list.Current;
                        DocUtils.TrimStart(prev);

                        while (list.MoveNext())
                        {
                            var now = list.Current;

                            if (now is LineBreak)
                            {
                                DocUtils.TrimEnd(prev);

                                if (list.MoveNext())
                                {
                                    now = list.Current;
                                    DocUtils.TrimStart(now);
                                }
                            }

                            prev = now;
                        }
                    }

                    if (prev is not null)
                        DocUtils.TrimEnd(prev);
                }

                if (inlines.Count > 0)
                {
                    var para = new Paragraph();
                    para.Inlines.AddRange(inlines);
                    return para;
                }
                return null;
            }

            List<Inline> stored = new();
            foreach (var e in elements)
            {
                if (e is Inline inline)
                {
                    stored.Add(inline);
                    continue;
                }

                // grouping inlines
                if (stored.Count != 0)
                {
                    var para = Group(stored);
                    if (para is not null) yield return para;
                    stored.Clear();
                }

                yield return (Block)e;
            }

            if (stored.Count != 0)
            {
                var para = Group(stored);
                if (para is not null) yield return para;
                stored.Clear();
            }
        }

        private static HtmlNode? PickBodyOrHead(HtmlNode documentNode, string headOrBody)
        {
            // html?
            foreach (var child in documentNode.ChildNodes)
            {
                if (child.Name == HtmlNode.HtmlNodeTypeNameText
                    || child.Name == HtmlNode.HtmlNodeTypeNameComment)
                    continue;

                switch (child.Name.ToLower())
                {
                    case "html":
                        // body? head?
                        foreach (var descendants in child.ChildNodes)
                        {
                            if (descendants.Name == HtmlNode.HtmlNodeTypeNameText
                                || descendants.Name == HtmlNode.HtmlNodeTypeNameComment)
                                continue;
                            switch (descendants.Name.ToLower())
                            {
                                case "head":
                                    if (headOrBody == "head")
                                        return descendants;
                                    break;

                                case "body":
                                    if (headOrBody == "body")
                                        return descendants;
                                    break;

                                default:
                                    return null;
                            }
                        }
                        break;

                    case "head":
                        if (headOrBody == "head")
                            return child;
                        break;

                    case "body":
                        if (headOrBody == "body")
                            return child;
                        break;

                    default:
                        return null;
                }
            }
            return null;
        }
    }
}
