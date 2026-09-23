using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GigaReplace.Core.Analysis.CSharp;

/// <summary>Records what a C# file declares, and which of its types look serialized.</summary>
internal sealed class CSharpFactsCollector : CSharpSyntaxWalker
{
    private static readonly HashSet<string> SerializerMethods = new(StringComparer.Ordinal)
    {
        "Serialize", "SerializeAsync", "SerializeToUtf8Bytes", "SerializeToElement", "SerializeToDocument",
        "SerializeToNode", "Deserialize", "DeserializeAsync", "DeserializeAsyncEnumerable", "SerializeObject",
        "DeserializeObject", "PopulateObject", "ReadFromJsonAsync", "GetFromJsonAsync", "PostAsJsonAsync",
        "PutAsJsonAsync", "PatchAsJsonAsync", "ToObject", "ReadAsAsync",
    };

    private static readonly HashSet<string> MemberSerializationAttributes = new(StringComparer.Ordinal)
    {
        "JsonPropertyName", "JsonProperty", "JsonIgnore", "JsonInclude", "JsonRequired", "JsonPropertyOrder",
        "DataMember", "IgnoreDataMember", "XmlElement", "XmlAttribute", "XmlIgnore", "XmlArray", "XmlArrayItem",
        "XmlText", "Column", "NotMapped", "BsonElement", "BsonId", "ProtoMember",
    };

    private static readonly HashSet<string> SerializerTypes = new(StringComparer.Ordinal)
    {
        "XmlSerializer", "DataContractSerializer", "DataContractJsonSerializer", "NetDataContractSerializer",
    };

    private readonly FileFacts _facts;
    private readonly string _path;
    private readonly Stack<string> _typeKeys = new();
    private readonly Stack<string> _typeNames = new();
    private string _namespace = "";

    private CSharpFactsCollector(FileFacts facts, string path)
    {
        _facts = facts;
        _path = path;
    }

    public static void Collect(SyntaxNode root, FileFacts facts, string path) => new CSharpFactsCollector(facts, path).Visit(root);

    private string CurrentTypeKey => _typeKeys.Count > 0 ? _typeKeys.Peek() : "";

    private string CurrentTypeName => _typeNames.Count > 0 ? _typeNames.Peek() : "";

    public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        var previous = _namespace;
        var name = Names.Dotted(node.Name);
        AddNamespaceParts(name);
        _namespace = Names.Combine(previous, name);
        base.VisitNamespaceDeclaration(node);
        _namespace = previous;
    }

    public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        var name = Names.Dotted(node.Name);
        AddNamespaceParts(name);
        _namespace = Names.Combine(_namespace, name);
        base.VisitFileScopedNamespaceDeclaration(node);
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node) => VisitType(node, () => base.VisitClassDeclaration(node));

    public override void VisitStructDeclaration(StructDeclarationSyntax node) => VisitType(node, () => base.VisitStructDeclaration(node));

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitType(node, () => base.VisitInterfaceDeclaration(node));

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node) => VisitType(node, () => base.VisitRecordDeclaration(node));

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node) => VisitType(node, () => base.VisitEnumDeclaration(node));

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        var name = node.Identifier.ValueText;
        _facts.Declared.Add(name);
        _facts.TypeNames.Add(name);
        if (_typeKeys.Count == 0)
        {
            _facts.Types.Add((_namespace, name));
        }
        else
        {
            _facts.Members.Add((CurrentTypeKey, name));
        }

        base.VisitDelegateDeclaration(node);
    }

    private void VisitType(BaseTypeDeclarationSyntax node, Action visitChildren)
    {
        string name = node.Identifier.ValueText;
        _facts.Declared.Add(name);
        _facts.TypeNames.Add(name);
        if (name.Length > 9 && name.EndsWith("Attribute", StringComparison.Ordinal))
        {
            _facts.Declared.Add(name[..^9]);
        }

        string key;
        if (_typeKeys.Count == 0)
        {
            _facts.Types.Add((_namespace, name));
            key = Names.Combine(_namespace, name);
        }
        else
        {
            _facts.Members.Add((CurrentTypeKey, name));
            key = CurrentTypeKey + "+" + name;
        }

        foreach (var attribute in node.AttributeLists.SelectMany(l => l.Attributes))
        {
            switch (Names.AttributeName(attribute.Name))
            {
                case "Serializable":
                    _facts.Serialized.Add((name, "marked [Serializable]"));
                    _facts.XmlContract.Add(name);
                    break;
                case "DataContract":
                    _facts.Serialized.Add((name, "marked [DataContract]"));
                    if (attribute.ArgumentList?.Arguments.Any(a => a.NameEquals?.Name.Identifier.ValueText == "Name") != true)
                    {
                        _facts.XmlContract.Add(name);
                    }

                    break;
                case "Table":
                    _facts.Serialized.Add((name, "mapped to a database table by [Table]"));
                    break;
                case "MessagePackObject" or "ProtoContract" or "JsonObject":
                    _facts.Serialized.Add((name, $"marked [{Names.AttributeName(attribute.Name)}]"));
                    break;
                case "JsonConverter" when node is EnumDeclarationSyntax && MentionsStringEnumConverter(attribute):
                    _facts.StringEnums.Add(name);
                    break;
            }
        }

        if (node is TypeDeclarationSyntax { BaseList: { } baseList }
            && baseList.Types.Any(t => Names.Simple(t.Type) == "ApplicationSettingsBase"))
        {
            _facts.SettingsTypes.Add(name);
        }

        if (node is TypeDeclarationSyntax { ParameterList: { } parameters })
        {
            foreach (var parameter in parameters.Parameters)
            {
                var parameterName = parameter.Identifier.ValueText;
                _facts.Declared.Add(parameterName);
                if (node is RecordDeclarationSyntax)
                {
                    // Positional record parameters become properties.
                    _facts.Members.Add((key, parameterName));
                    AddMemberTypes(name, parameter.Type);
                }
                else
                {
                    _facts.Locals.Add((key, parameterName));
                }
            }
        }

        _typeKeys.Push(key);
        _typeNames.Push(name);
        visitChildren();
        _typeKeys.Pop();
        _typeNames.Pop();
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var name = node.Identifier.ValueText;
        AddMember(name);
        if (Names.AttributeNames(node.AttributeLists).Contains("RelayCommand"))
        {
            var baseName = name.Length > 5 && name.EndsWith("Async", StringComparison.Ordinal) ? name[..^5] : name;
            AddMember(baseName + "Command");
        }

        base.VisitMethodDeclaration(node);
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        AddMember(node.Identifier.ValueText);
        AddMemberTypes(CurrentTypeName, node.Type);
        NoteMemberSerializationAttributes(node.AttributeLists);
        base.VisitPropertyDeclaration(node);
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        bool observable = Names.AttributeNames(node.AttributeLists).Contains("ObservableProperty");
        foreach (var variable in node.Declaration.Variables)
        {
            var name = variable.Identifier.ValueText;
            AddMember(name);
            if (observable && Names.ObservablePropertyName(name) is { } generated)
            {
                AddMember(generated);
            }
        }

        AddMemberTypes(CurrentTypeName, node.Declaration.Type);
        NoteMemberSerializationAttributes(node.AttributeLists);
        base.VisitFieldDeclaration(node);
    }

    public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
    {
        foreach (var variable in node.Declaration.Variables)
        {
            AddMember(variable.Identifier.ValueText);
        }

        base.VisitEventFieldDeclaration(node);
    }

    public override void VisitEventDeclaration(EventDeclarationSyntax node)
    {
        AddMember(node.Identifier.ValueText);
        base.VisitEventDeclaration(node);
    }

    public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node)
    {
        AddMember(node.Identifier.ValueText);
        base.VisitEnumMemberDeclaration(node);
    }

    public override void VisitParameter(ParameterSyntax node)
    {
        // Primary-constructor parameters are handled with their type.
        if (node.Parent?.Parent is not TypeDeclarationSyntax && node.Identifier.ValueText.Length > 0)
        {
            AddLocal(node.Identifier.ValueText);
        }

        base.VisitParameter(node);
    }

    public override void VisitTypeParameter(TypeParameterSyntax node)
    {
        AddLocal(node.Identifier.ValueText);
        base.VisitTypeParameter(node);
    }

    public override void VisitVariableDeclarator(VariableDeclaratorSyntax node)
    {
        if (node.Parent?.Parent is not BaseFieldDeclarationSyntax)
        {
            AddLocal(node.Identifier.ValueText);
        }

        base.VisitVariableDeclarator(node);
    }

    public override void VisitSingleVariableDesignation(SingleVariableDesignationSyntax node)
    {
        AddLocal(node.Identifier.ValueText);
        base.VisitSingleVariableDesignation(node);
    }

    public override void VisitForEachStatement(ForEachStatementSyntax node)
    {
        AddLocal(node.Identifier.ValueText);
        base.VisitForEachStatement(node);
    }

    public override void VisitCatchDeclaration(CatchDeclarationSyntax node)
    {
        if (node.Identifier.ValueText.Length > 0)
        {
            AddLocal(node.Identifier.ValueText);
        }

        base.VisitCatchDeclaration(node);
    }

    public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        AddLocal(node.Identifier.ValueText);
        base.VisitLocalFunctionStatement(node);
    }

    public override void VisitLabeledStatement(LabeledStatementSyntax node)
    {
        AddLocal(node.Identifier.ValueText);
        base.VisitLabeledStatement(node);
    }

    public override void VisitFromClause(FromClauseSyntax node)
    {
        AddLocal(node.Identifier.ValueText);
        base.VisitFromClause(node);
    }

    public override void VisitLetClause(LetClauseSyntax node)
    {
        AddLocal(node.Identifier.ValueText);
        base.VisitLetClause(node);
    }

    public override void VisitJoinClause(JoinClauseSyntax node)
    {
        AddLocal(node.Identifier.ValueText);
        base.VisitJoinClause(node);
    }

    public override void VisitJoinIntoClause(JoinIntoClauseSyntax node)
    {
        AddLocal(node.Identifier.ValueText);
        base.VisitJoinIntoClause(node);
    }

    public override void VisitQueryContinuation(QueryContinuationSyntax node)
    {
        AddLocal(node.Identifier.ValueText);
        base.VisitQueryContinuation(node);
    }

    public override void VisitAnonymousObjectMemberDeclarator(AnonymousObjectMemberDeclaratorSyntax node)
    {
        if (node.NameEquals is { } nameEquals)
        {
            _facts.Declared.Add(nameEquals.Name.Identifier.ValueText);
        }

        base.VisitAnonymousObjectMemberDeclarator(node);
    }

    public override void VisitTupleElement(TupleElementSyntax node)
    {
        if (node.Identifier.ValueText.Length > 0)
        {
            _facts.Declared.Add(node.Identifier.ValueText);
        }

        base.VisitTupleElement(node);
    }

    public override void VisitArgument(ArgumentSyntax node)
    {
        if (node.NameColon is { } nameColon && node.Parent is TupleExpressionSyntax)
        {
            _facts.Declared.Add(nameColon.Name.Identifier.ValueText);
        }

        base.VisitArgument(node);
    }

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        if (node.Alias is { } alias)
        {
            _facts.Declared.Add(alias.Name.Identifier.ValueText);
        }

        base.VisitUsingDirective(node);
    }

    public override void VisitExternAliasDirective(ExternAliasDirectiveSyntax node)
    {
        _facts.Declared.Add(node.Identifier.ValueText);
        base.VisitExternAliasDirective(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var (name, receiver, generic) = node.Expression switch
        {
            MemberAccessExpressionSyntax ma => (ma.Name.Identifier.ValueText, ma.Expression.ToString(), ma.Name as GenericNameSyntax),
            MemberBindingExpressionSyntax mb => (mb.Name.Identifier.ValueText, "", mb.Name as GenericNameSyntax),
            GenericNameSyntax g => (g.Identifier.ValueText, "", g),
            IdentifierNameSyntax i => (i.Identifier.ValueText, "", (GenericNameSyntax?)null),
            _ => ("", "", null),
        };

        string? reason = null;
        if (SerializerMethods.Contains(name))
        {
            reason = $"passed to {(receiver.Length > 0 ? Shorten(receiver) + "." : "")}{name} in {_path}";
        }
        else if (name is "Configure" or "AddOptions" or "ConfigureOptions")
        {
            reason = "bound from configuration";
        }
        else if (name is "Get" or "GetValue" or "Bind"
            && (receiver.Contains("config", StringComparison.OrdinalIgnoreCase) || receiver.Contains("section", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "bound from configuration";
        }

        if (reason is not null)
        {
            if (generic is not null)
            {
                foreach (var type in generic.TypeArgumentList.Arguments)
                {
                    AddSerialized(type, reason);
                }
            }

            foreach (var argument in node.ArgumentList.Arguments)
            {
                if (argument.Expression is TypeOfExpressionSyntax typeOf)
                {
                    AddSerialized(typeOf.Type, reason);
                }
            }
        }

        base.VisitInvocationExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var typeName = Names.Simple(node.Type);
        if (typeName is not null && SerializerTypes.Contains(typeName) && node.ArgumentList is { } arguments)
        {
            foreach (var argument in arguments.Arguments)
            {
                if (argument.Expression is TypeOfExpressionSyntax typeOf)
                {
                    AddSerialized(typeOf.Type, $"serialized with {typeName} in {_path}");
                    _facts.XmlContract.AddRange(Names.TypeNamesOf(typeOf.Type));
                }
            }
        }
        else if (typeName == "JsonStringEnumConverter")
        {
            _facts.UsesStringEnumConverter = true;
        }

        base.VisitObjectCreationExpression(node);
    }

    public override void VisitAttribute(AttributeSyntax node)
    {
        var name = Names.AttributeName(node.Name);
        if (name == "JsonSerializable" && node.ArgumentList is { } arguments)
        {
            foreach (var argument in arguments.Arguments)
            {
                if (argument.Expression is TypeOfExpressionSyntax typeOf)
                {
                    AddSerialized(typeOf.Type, "listed in a [JsonSerializable] context");
                }
            }
        }
        else if (name == "JsonConverter" && node.Parent?.Parent is not EnumDeclarationSyntax && MentionsStringEnumConverter(node))
        {
            // On a property or options; an enum's own attribute only affects that enum.
            _facts.UsesStringEnumConverter = true;
        }

        base.VisitAttribute(node);
    }

    public override void VisitGenericName(GenericNameSyntax node)
    {
        string? reason = node.Identifier.ValueText switch
        {
            "DbSet" => "an Entity Framework entity (DbSet<>)",
            "IOptions" or "IOptionsMonitor" or "IOptionsSnapshot" => "bound from configuration (IOptions<>)",
            _ => null,
        };

        if (reason is not null)
        {
            foreach (var type in node.TypeArgumentList.Arguments)
            {
                AddSerialized(type, reason);
            }
        }

        base.VisitGenericName(node);
    }

    private static bool MentionsStringEnumConverter(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments.Any(a => a.Expression is TypeOfExpressionSyntax t && Names.Simple(t.Type) == "JsonStringEnumConverter") == true
        || (attribute.Name is GenericNameSyntax g && g.TypeArgumentList.Arguments.Any(t => Names.Simple(t) == "JsonStringEnumConverter"));

    private void NoteMemberSerializationAttributes(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var name in Names.AttributeNames(lists))
        {
            if (MemberSerializationAttributes.Contains(name))
            {
                _facts.Serialized.Add((CurrentTypeName, $"its members use [{name}]"));
                return;
            }
        }
    }

    private void AddSerialized(TypeSyntax type, string reason)
    {
        foreach (var name in Names.TypeNamesOf(type))
        {
            _facts.Serialized.Add((name, reason));
        }
    }

    private void AddMemberTypes(string owner, TypeSyntax? type)
    {
        if (owner.Length == 0)
        {
            return;
        }

        foreach (var name in Names.TypeNamesOf(type))
        {
            _facts.MemberTypes.Add((owner, name));
        }
    }

    private void AddNamespaceParts(string dotted)
    {
        foreach (var part in dotted.Split('.'))
        {
            _facts.Declared.Add(part);
        }
    }

    private void AddMember(string name)
    {
        _facts.Declared.Add(name);
        _facts.Members.Add((CurrentTypeKey, name));
    }

    private void AddLocal(string name)
    {
        _facts.Declared.Add(name);
        _facts.Locals.Add((CurrentTypeKey, name));
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..37] + "…";
}
