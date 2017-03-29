﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace Amadevus.RecordGenerator
{
    internal abstract class RecordPartialGenerator
    {
        protected RecordPartialGenerator(TypeDeclarationSyntax declaration, CancellationToken cancellationToken)
        {
            TypeDeclaration = declaration;
            CancellationToken = cancellationToken;
            TypeSyntaxLazy = new Lazy<TypeSyntax>(GetTypeSyntax);
            RecordPropertiesLazy = new Lazy<ImmutableArray<RecordEntry>>(GetRecordProperties);
        }

        protected TypeDeclarationSyntax TypeDeclaration { get; }

        protected CancellationToken CancellationToken { get; }

        protected IReadOnlyList<RecordEntry> RecordProperties => RecordPropertiesLazy.Value;

        protected TypeSyntax RecordTypeSyntax => TypeSyntaxLazy.Value;

        private Lazy<ImmutableArray<RecordEntry>> RecordPropertiesLazy { get; }

        private Lazy<TypeSyntax> TypeSyntaxLazy { get; }

        public static Document GenerateRecordPartialDocument(Document document, TypeDeclarationSyntax declaration, INamedTypeSymbol typeSymbol, CancellationToken cancellationToken)
        {
            var generator = Create(declaration, cancellationToken);
            return generator.GenerateRecordPartial(document, typeSymbol);
        }

        public static CompilationUnitSyntax GenerateRecordPartialRoot(TypeDeclarationSyntax declaration, CancellationToken cancellationToken)
        {
            var generator = Create(declaration, cancellationToken);
            return generator.GenerateCompilationUnit();
        }

        public static TypeDeclarationSyntax GetGeneratedPartial(TypeDeclarationSyntax typeDeclaration, INamedTypeSymbol typeSymbol)
        {
            var syntaxRefs = typeSymbol.DeclaringSyntaxReferences;
            if (syntaxRefs.Length == 1)
            {
                return null;
            }
            // get all partial declarations (except the original one)
            var declarations =
                syntaxRefs
                .Select(@ref => @ref.GetSyntax() as TypeDeclarationSyntax)
                .Where(syntax => syntax != null && syntax != typeDeclaration)
                .ToList();

            // find the one with appropriate header
            var recordPartial = declarations.FirstOrDefault(d => d.IsFileHeaderPresent());
            return recordPartial;
        }

        protected static RecordPartialGenerator Create(TypeDeclarationSyntax declaration, CancellationToken cancellationToken)
        {
            if (declaration is ClassDeclarationSyntax classDeclaration)
            {
                return new ClassRecordPartialGenerator(classDeclaration, cancellationToken);
            }
            if (declaration is StructDeclarationSyntax structDeclaration)
            {
                return new StructRecordPartialGenerator(structDeclaration, cancellationToken);
            }
            return null;
        }

        protected abstract Document GenerateRecordPartial(Document document, INamedTypeSymbol typeSymbol);

        protected abstract string TypeName();

        protected Document GenerateDocument(Document document, INamedTypeSymbol typeSymbol)
        {
            var compilationUnit = GenerateCompilationUnit();
            var partialDocument = DocumentFrom(compilationUnit, document, typeSymbol);
            return partialDocument;
        }

        protected CompilationUnitSyntax GenerateCompilationUnit()
        {
            var typeDeclaration = GenerateTypeDeclaration();
            var rootMemberDeclaration = RootMemberDeclarationFrom(typeDeclaration);
            var compilationUnit = CompilationUnitFrom(rootMemberDeclaration);
            return compilationUnit;
        }

        protected Document DocumentFrom(CompilationUnitSyntax compilationUnit, Document document, INamedTypeSymbol typeSymbol)
        {
            var typeName = TypeName();
            var project = document.Project;
            var recordPartialRoot = FormattedPerWorkspace(compilationUnit, project.Solution.Workspace);

            if (GetGeneratedPartial(TypeDeclaration, typeSymbol) is TypeDeclarationSyntax existingPartial)
            {
                var existingPartialDocument = project.GetDocument(existingPartial.SyntaxTree);
                return existingPartialDocument.WithSyntaxRoot(recordPartialRoot);
            }
            var recordPartialDocument = project.AddDocument($"{typeName}.{RecordPartialProperties.FilenamePostfix}.cs", recordPartialRoot, document.Folders);
            return recordPartialDocument;
        }

        protected CompilationUnitSyntax FormattedPerWorkspace(CompilationUnitSyntax compilationUnit, Workspace workspace)
        {
            var formatted = Formatter.Format(compilationUnit, workspace, cancellationToken: CancellationToken);
            return (CompilationUnitSyntax)formatted;
        }

        protected CompilationUnitSyntax CompilationUnitFrom(MemberDeclarationSyntax rootMemberDeclaration)
        {
            var syntaxTree = TypeDeclaration.SyntaxTree;
            var recordPartialCompilationUnit = SyntaxFactory.CompilationUnit()
                .WithUsings(syntaxTree.GetCompilationUnitRoot(CancellationToken).Usings)
                .WithMembers(SyntaxFactory.SingletonList(rootMemberDeclaration))
                .WithLeadingTrivia(
                    SyntaxFactory.SyntaxTrivia(SyntaxKind.SingleLineCommentTrivia, RecordPartialProperties.FileHeader),
                    SyntaxFactory.SyntaxTrivia(SyntaxKind.EndOfLineTrivia, Environment.NewLine),
                    SyntaxFactory.SyntaxTrivia(SyntaxKind.EndOfLineTrivia, Environment.NewLine));

            return recordPartialCompilationUnit;
        }

        protected MemberDeclarationSyntax RootMemberDeclarationFrom(TypeDeclarationSyntax newTypeDeclaration)
        {
            var newRootMemberDeclaration =
                TypeDeclaration
                .Ancestors()
                .OfType<NamespaceDeclarationSyntax>()
                .Aggregate(newTypeDeclaration as MemberDeclarationSyntax, (prev, curr) =>
                {
                    return curr.WithMembers(SyntaxFactory.SingletonList(prev));
                });
            return newRootMemberDeclaration;
        }

        protected abstract TypeDeclarationSyntax GenerateTypeDeclaration();
        
        protected SyntaxList<MemberDeclarationSyntax> GenerateMembers(SyntaxToken identifier, IReadOnlyList<RecordEntry> properties)
        {
            var ctor = SyntaxFactory.ConstructorDeclaration(identifier.ValueText)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithParameterList(properties.IntoCtorParameterList())
                .WithBody(properties.IntoCtorBody());

            return SyntaxFactory.SingletonList<MemberDeclarationSyntax>(ctor)
                .AddRange(RecordProperties.Select(p => MutatorFrom(p)));
        }

        protected MethodDeclarationSyntax MutatorFrom(RecordEntry entry)
        {
            var arguments = RecordProperties.Select(x =>
            {
                return SyntaxFactory.Argument(
                    SyntaxFactory.IdentifierName(x.Identifier));
            });

            var mutator =
                SyntaxFactory.MethodDeclaration(RecordTypeSyntax, MutatorIdentifierFor(entry))
                .WithModifiers(
                    SyntaxFactory.TokenList(
                        SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithParameterList(
                    SyntaxFactory.ParameterList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Parameter(
                                entry.Identifier)
                                .WithType(entry.Type))))
                .WithBody(
                    SyntaxFactory.Block(
                        SyntaxFactory.ReturnStatement(
                            SyntaxFactory.ObjectCreationExpression(RecordTypeSyntax)
                            .WithArgumentList(
                                SyntaxFactory.ArgumentList(
                                    SyntaxFactory.SeparatedList(arguments))))));
            return mutator;
        }

        protected SyntaxToken MutatorIdentifierFor(RecordEntry entry)
        {
            return SyntaxFactory.Identifier($"With{entry.Identifier.ValueText}");
        }

        protected ImmutableArray<RecordEntry> GetRecordProperties()
        {
            return TypeDeclaration.Members.GetRecordProperties().AsRecordEntries();
        }

        private TypeSyntax GetTypeSyntax()
        {
            var typeParamList = TypeDeclaration.TypeParameterList;
            if (typeParamList == null)
            {
                return SyntaxFactory.IdentifierName(TypeDeclaration.Identifier);
            }

            var arguments = typeParamList.Parameters.Select(param => SyntaxFactory.IdentifierName(param.Identifier));
            var typeArgList =
                SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SeparatedList<TypeSyntax>(
                        arguments));

            return SyntaxFactory.GenericName(TypeDeclaration.Identifier, typeArgList);
        }
    }
}