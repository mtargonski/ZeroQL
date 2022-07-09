﻿using System;
using System.Collections.Generic;
using System.Linq;
using GraphQLParser;
using GraphQLParser.AST;
using LinqQL.Core.Extensions;
using LinqQL.Core.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace LinqQL.Core.Bootstrap;

public static class GraphQLGenerator
{

    public static string ToCSharp(string graphql, string clientNamespace, string? clientName)
    {
        var schema = Parser.Parse(graphql);
        var enums = schema.Definitions
            .OfType<GraphQLEnumTypeDefinition>()
            .ToArray();

        var enumsNames = new HashSet<string>(enums.Select(o => o.Name.StringValue));

        var context = new TypeFormatter(enumsNames);
        var inputs = schema.Definitions
            .OfType<GraphQLInputObjectTypeDefinition>()
            .Select(o => CreateInputDefinition(context, o))
            .ToArray();

        var types = schema.Definitions
            .OfType<GraphQLObjectTypeDefinition>()
            .Select(o => CreateTypesDefinition(context, o))
            .ToArray();


        var namespaceDeclaration = NamespaceDeclaration(IdentifierName(clientNamespace));
        var clientDeclaration = new[] { GenerateClient(clientName) };
        var typesDeclaration = GenerateTypes(types);
        var inputsDeclaration = GenerateInputs(inputs);
        var enumsDeclaration = GenerateEnums(enums);

        namespaceDeclaration = namespaceDeclaration
            .WithMembers(List<MemberDeclarationSyntax>(clientDeclaration)
                .AddRange(typesDeclaration)
                .AddRange(inputsDeclaration)
                .AddRange(enumsDeclaration));

        var formattedSource = namespaceDeclaration.NormalizeWhitespace().ToFullString();
        return $@"// This file generated for LinqQL.
// <auto-generated/>
using System; 
using System.Linq; 
using System.Text.Json.Serialization; 

{formattedSource}";
    }

    private static ClassDeclarationSyntax GenerateClient(string? clientName)
        => CSharpHelper.Class(clientName ?? "GraphQLClient")
            .WithBaseList(BaseList(SingletonSeparatedList<BaseTypeSyntax>(SimpleBaseType(IdentifierName("global::LinqQL.Core.GraphQLClient<Query, Mutation>")))))
            .WithMembers(SingletonList<MemberDeclarationSyntax>(
                ConstructorDeclaration(clientName ?? "GraphQLClient")
                    .WithParameterList(ParseParameterList("(global::System.Net.Http.HttpClient client)"))
                    // call base constructor
                    .WithInitializer(ConstructorInitializer(SyntaxKind.BaseConstructorInitializer,
                        ArgumentList(SingletonSeparatedList<ArgumentSyntax>(
                            Argument(IdentifierName("client"))
                        )))
                    )
                    .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                    .WithBody(Block())));

    private static ClassDeclarationSyntax[] GenerateInputs(ClassDefinition[] inputs)
    {
        return inputs
            .Select(o =>
            {
                var fields = o.Properties
                    .Select(property => CSharpHelper.Property(property.Name, property.TypeDefinition.Name + property.TypeDefinition.NullableAnnotation()));

                return CSharpHelper.Class(o.Name)
                    .AddAttributes(SourceGeneratorInfo.CodeGenerationAttribute)
                    .WithMembers(List<MemberDeclarationSyntax>(fields));

            })
            .ToArray();
    }

    private static ClassDefinition CreateInputDefinition(TypeFormatter typeFormatter, GraphQLInputObjectTypeDefinition input)
    {
        var typeDefinition = new ClassDefinition
        {
            Name = input.Name.StringValue,
            Properties = CretePropertyDefinition(typeFormatter, input)
        };

        return typeDefinition;
    }

    private static EnumDeclarationSyntax[] GenerateEnums(GraphQLEnumTypeDefinition[] enums)
    {
        return enums.Select(e =>
            {
                var members = e.Values.Select(o =>
                    {
                        var name = o.Name.StringValue;
                        return EnumMemberDeclaration(Identifier(name));
                    })
                    .ToArray();

                var enumSyntax = EnumDeclaration(Identifier(e.Name.StringValue))
                    .AddAttributeLists(AttributeList()
                        .AddAttributes(Attribute(ParseName(SourceGeneratorInfo.CodeGenerationAttribute))))
                    .AddMembers(members)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword));

                return enumSyntax;
            })
            .ToArray();
    }

    private static ClassDeclarationSyntax[] GenerateTypes(ClassDefinition[] classes)
    {
        return classes
            .Select(o =>
            {
                var backedFields = o.Properties
                    .Where(property => property.TypeDefinition is ObjectTypeDefinition or ListTypeDefinition)
                    .Select(property =>
                    {
                        var jsonNameAttributes = new[]
                        {
                            ("global::System.ComponentModel.EditorBrowsable", "global::System.ComponentModel.EditorBrowsableState.Never"),
                            ("JsonPropertyName", Literal(property.Name).Text)
                        };

                        return CSharpHelper
                            .Property("__" + property.Name, property.TypeDefinition.Name)
                            .AddAttributes(jsonNameAttributes);
                    });

                var fields = o.Properties.Select(GeneratePropertiesDeclarations);
                return CSharpHelper.Class(o.Name)
                    .AddAttributes(SourceGeneratorInfo.CodeGenerationAttribute)
                    .WithMembers(List<MemberDeclarationSyntax>(backedFields).AddRange(fields));

            })
            .ToArray();
    }

    private static MemberDeclarationSyntax GeneratePropertiesDeclarations(FieldDefinition field)
    {
        if (field.TypeDefinition is ObjectTypeDefinition or ListTypeDefinition)
        {
            var parameters = field.Arguments
                .Select(o =>
                    Parameter(Identifier(o.Name))
                        .WithType(ParseTypeName(o.TypeName)))
                .ToArray();

            return GenerateQueryPropertyDeclaration(field, parameters);
        }

        return CSharpHelper.Property(field.Name, field.TypeDefinition.NameWithNullableAnnotation());
    }


    private static MemberDeclarationSyntax GenerateQueryPropertyDeclaration(FieldDefinition field, ParameterSyntax[] parameters)
    {
        var returnType = GetPropertyReturnType(field.TypeDefinition);
        var name = GetPropertyName(field.Name, field.TypeDefinition);
        var methodBody = $"return {GetPropertyMethodBody("__" + field.Name, field.TypeDefinition)};";

        var funcType = GetPropertyFuncType(field.TypeDefinition);
        var selectorParameter = Parameter(Identifier("selector")).WithType(ParseTypeName($"Func<{funcType}, T>"));

        var list = SeparatedList(parameters);
        if (RequireSelector(field.TypeDefinition))
        {
            list = list.Add(selectorParameter);
        }

        var genericMethodWithType = MethodDeclaration(
                IdentifierName(returnType),
                Identifier(name))
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .WithParameterList(ParameterList(list));

        var body = Block(
            ParseStatement(methodBody));

        return genericMethodWithType
            .WithBody(body);
    }

    private static bool RequireSelector(TypeDefinition typeDefinition)
    {
        switch (typeDefinition)
        {
            case ObjectTypeDefinition:
                return true;
            case ScalarTypeDefinition:
            case EnumTypeDefinition:
                return false;
            case ListTypeDefinition type:
                return RequireSelector(type.ElementTypeDefinition);
            default:
                throw new NotImplementedException();
        }
    }

    private static string GetPropertyName(string fieldName, TypeDefinition typeDefinition)
    {
        switch (typeDefinition)
        {
            case ObjectTypeDefinition:
                return fieldName + "<T>";
            case ScalarTypeDefinition:
            case EnumTypeDefinition:
                return fieldName;
            case ListTypeDefinition type:
                return GetPropertyName(fieldName, type.ElementTypeDefinition);
            default:
                throw new NotImplementedException();
        }
    }

    private static string GetPropertyFuncType(TypeDefinition typeDefinition)
    {
        switch (typeDefinition)
        {
            case ObjectTypeDefinition:
            case ScalarTypeDefinition:
            case EnumTypeDefinition:
                return typeDefinition.Name + typeDefinition.NullableAnnotation();
            case ListTypeDefinition type:
                return GetPropertyFuncType(type.ElementTypeDefinition);
            default:
                throw new NotImplementedException();
        }
    }

    private static string GetPropertyMethodBody(string fieldName, TypeDefinition typeDefinition)
    {
        switch (typeDefinition)
        {
            case ScalarTypeDefinition:
            case EnumTypeDefinition:
                return fieldName;
            case ObjectTypeDefinition:
                return $"{fieldName} != default ? selector({fieldName}) : default";
            case ListTypeDefinition { ElementTypeDefinition: ScalarTypeDefinition or EnumTypeDefinition }:
                return fieldName;
            case ListTypeDefinition type:
                return $"{fieldName}?.Select(o => {GetPropertyMethodBody("o", type.ElementTypeDefinition)}).ToArray()";
            default:
                throw new NotImplementedException();
        }
    }

    private static string GetPropertyReturnType(TypeDefinition typeDefinition)
    {
        switch (typeDefinition)
        {
            case ObjectTypeDefinition type:
                return "T" + type.NullableAnnotation();
            case ScalarTypeDefinition type:
                return type.NameWithNullableAnnotation();
            case EnumTypeDefinition type:
                return type.NameWithNullableAnnotation();
            case ListTypeDefinition type:
                return $"{GetPropertyReturnType(type.ElementTypeDefinition)}[]{type.NullableAnnotation()}";
            default:
                throw new NotImplementedException();
        }
    }

    private static ClassDefinition CreateTypesDefinition(TypeFormatter typeFormatter, GraphQLObjectTypeDefinition type)
    {
        var typeDefinition = new ClassDefinition
        {
            Name = type.Name.StringValue,
            Properties = CretePropertyDefinition(typeFormatter, type)
        };

        return typeDefinition;
    }

    private static FieldDefinition[] CretePropertyDefinition(TypeFormatter typeFormatter, GraphQLInputObjectTypeDefinition typeQL)
    {
        return typeQL.Fields?.Select(field =>
            {
                var type = typeFormatter.GetTypeDefinition(field.Type);
                return new FieldDefinition
                {
                    Name = field.Name.StringValue.FirstToUpper(),
                    TypeDefinition = type,
                    Arguments = Array.Empty<ArgumentDefinition>()
                };
            })
            .ToArray() ?? Array.Empty<FieldDefinition>();
    }

    private static FieldDefinition[] CretePropertyDefinition(TypeFormatter typeFormatter, GraphQLObjectTypeDefinition typeQL)
    {
        return typeQL.Fields?.Select(field =>
            {
                var type = typeFormatter.GetTypeDefinition(field.Type);
                return new FieldDefinition
                {
                    Name = field.Name.StringValue.FirstToUpper(),
                    TypeDefinition = type,
                    Arguments = field.Arguments?
                        .Select(arg => new ArgumentDefinition { Name = arg.Name.StringValue, TypeName = typeFormatter.GetTypeDefinition(arg.Type).Name })
                        .ToArray() ?? Array.Empty<ArgumentDefinition>()
                };
            })
            .ToArray() ?? Array.Empty<FieldDefinition>();
    }
}