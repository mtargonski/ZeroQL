using System.Reflection;
using FluentAssertions;
using LinqQL.Core;
using LinqQL.SourceGenerators;
using LinqQL.SourceGenerators.Analyzers;
using LinqQL.SourceGenerators.Generator;
using LinqQL.Tests.Core;
using LinqQL.Tests.Data;
using Xunit;

namespace LinqQL.Tests.SourceGeneration;

public class QueryTests : IntegrationTest
{

    [Fact]
    public async Task CompilationWorks()
    {
        await TestProject.Project.CompileToRealAssembly();
    }

    [Fact]
    public async Task SimpleQuery()
    {
        var graphqlQuery = @"query { me { firstName } }";
        var project = TestProject.Project;

        var result = (GraphQLResult<string>)await project.Validate(graphqlQuery);

        result.Data.Should().Be("Jon");
    }

    [Fact]
    public async Task SupportForAnonymousType()
    {
        var csharpQuery = "static q => q.Me(o => new { o.FirstName })";
        var graphqlQuery = @"{ me { firstName } }";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, csharpQuery));

        var assembly = await project.CompileToRealAssembly();

        await assembly.ExecuteRequest();

        GraphQLQueryStore.Query[csharpQuery].Should().Be(graphqlQuery);
    }

    [Fact]
    public async Task NullableQueryHandled()
    {
        var csharpQuery = "static q => q.User(10, o => o.FirstName)";
        var graphqlQuery = @"query { user(id: 10) { firstName } }";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, csharpQuery));

        var result = await project.Validate(graphqlQuery);

        result.Query.Should().Be(graphqlQuery);
    }

    [Fact]
    public async Task SupportForMultipleMembers()
    {
        var csharpQuery = "static q => q.Me(o => new { o.FirstName.Length })";
        var graphqlQuery = @"{ me { firstName } }";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, csharpQuery));

        var assembly = await project.CompileToRealAssembly();

        await assembly.ExecuteRequest();

        GraphQLQueryStore.Query[csharpQuery].Should().Be(graphqlQuery);
    }

    [Fact]
    public async Task FailsWhenAccessingVariablesOutLambdaScope()
    {
        var csharpQuery = "static q => q.Me(o => q.Me(o => o.FirstName))";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, csharpQuery));

        var diagnostics = await project.ApplyGenerator(new GraphQLQuerySourceGenerator());

        diagnostics.Should()
            .Contain(o => o.Id == Descriptors.DontUserOutScopeValues.Id);
    }

    [Fact]
    public async Task FailsOnOpenLambda()
    {
        var csharpQuery = "static q => q.Me(o => o)";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, csharpQuery));

        var diagnostics = await project.ApplyAnalyzer(new QueryLambdaAnalyzer());
        diagnostics.Should()
            .Contain(o => o.Id == Descriptors.OpenLambdaIsNotAllowed.Id);
    }

    [Fact]
    public async Task FailsWhenLambdaIsNotStatic()
    {
        var csharpQuery = "q => q.Me(o => o.FirstName)";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, csharpQuery));

        var diagnostics = await project.ApplyAnalyzer(new QueryLambdaAnalyzer());

        diagnostics.Should()
            .Contain(o => o.Id == Descriptors.OnlyStaticLambda.Id);
    }

    [Fact]
    public async Task SupportsAnonymousTypeInQueryRoot()
    {
        var csharpQuery = "static q => new { Me = q.Me(o => new { o.FirstName }) }";
        var graphqlQuery = @"query { me { firstName } }";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, csharpQuery));

        var assembly = await project.CompileToRealAssembly();

        var response = await assembly.ExecuteRequest();

        response.Query.Should().Be(graphqlQuery);
    }

    [Fact]
    public async Task SupportsAnonymousTypeWithMultipleFields()
    {
        var csharpQuery = "static q => q.Me(o => new { o.FirstName, o.LastName })";
        var graphqlQuery = @"query { me { firstName lastName } }";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, csharpQuery));

        var assembly = await project.CompileToRealAssembly();
        var response = await assembly.ExecuteRequest();

        response.Query.Should().Be(graphqlQuery);
    }

    [Fact(Skip = "Think how to fix after release")]
    public async Task SupportsAnonymousTypeWithMultipleIdenticalFieldsInRootQuery()
    {
        var csharpQuery = "static q => new { Me1 = q.Me(o => new { o.FirstName, o.LastName }), Me2 = q.Me(o => new { o.FirstName, o.LastName }) }";
        var graphqlQuery = @"{ m1: me { firstName lastName } m2: me { firstName lastName } }";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, csharpQuery));

        var assembly = await project.CompileToRealAssembly();

        await assembly.ExecuteRequest();

        GraphQLQueryStore.Query[csharpQuery].Should().Be(graphqlQuery);
    }

    [Fact]
    public async Task SupportsAnonymousTypeWithConstantArgumentQuery()
    {
        var csharpQuery = "static q => new { User = q.User(42, o => new { o.FirstName, o.LastName }) }";
        var graphqlQuery = @"{ user(id: 42) { firstName lastName } }";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, csharpQuery));

        var assembly = await project.CompileToRealAssembly();

        await assembly.ExecuteRequest();

        GraphQLQueryStore.Query[csharpQuery].Should().Be(graphqlQuery);
    }

    [Fact]
    public async Task SupportForEnums()
    {
        var csharpQuery = "static q => q.Me(o => o.UserKind)";
        var graphqlQuery = @"{ me { userKind } }";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, csharpQuery));

        var assembly = await project.CompileToRealAssembly();
        await assembly.ExecuteRequest();

        GraphQLQueryStore.Query[csharpQuery].Should().Be(graphqlQuery);
    }

    [Fact]
    public async Task SupportForArray()
    {
        var arguments = "new { Filter = new UserFilterInput { UserKind = UserKind.GOOD} }";
        var csharpQuery = "static (i, q) => q.Users(i.Filter, 0,  10, o => o.FirstName)";
        var graphqlQuery = @"($filter: UserFilterInput!) { users(filter: $filter, page: 0, size: 10) { firstName } }";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, $"{arguments}, {csharpQuery}"));

        var assembly = await project.CompileToRealAssembly();
        await assembly.ExecuteRequest();

        GraphQLQueryStore.Query[csharpQuery].Should().Be(graphqlQuery);
    }

    [Fact]
    public async Task SupportForArrayWithScalarElements()
    {
        var csharpQuery = "static q => q.UsersIds(UserKind.GOOD, 0, 10)";
        var graphqlQuery = @"{ usersIds(kind: GOOD, page: 0, size: 10)}";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, csharpQuery));

        var assembly = await project.CompileToRealAssembly();
        await assembly.ExecuteRequest();

        GraphQLQueryStore.Query[csharpQuery].Should().Be(graphqlQuery);
    }

    [Fact]
    public async Task SupportsQueryName()
    {
        var csharpQuery = "static q => q.Me(o => o.FirstName)";
        var graphqlQuery = @"{ me { firstName } }";

        var project = await TestProject.Project
            .ReplacePartOfDocumentAsync("Program.cs", (TestProject.MeQuery, @"""Me"", " + csharpQuery));

        var assembly = await project.CompileToRealAssembly();
        await assembly.ExecuteRequest();

        GraphQLQueryStore.Query[csharpQuery].Should().Be(graphqlQuery);
    }
}