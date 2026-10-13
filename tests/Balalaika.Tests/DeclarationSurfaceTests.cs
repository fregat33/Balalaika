using FluentAssertions;
using Balalaika.Index;

namespace Balalaika.Tests;

public sealed class DeclarationSurfaceTests
{
    private static readonly SymbolRecord Type = new("T:A", "class", "A", "A", "public class A", "public", null, 1, 14);
    private static readonly SymbolRecord PublicMethod = new("M:A.M", "method", "M", "A.M", "public void M()", "public", "T:A", 3, 17);
    private static readonly SymbolRecord PrivateField = new("F:A._n", "field", "_n", "A._n", "private int _n", "private", "T:A", 5, 17);
    private static readonly RelationRecord Implements = new("T:A", "T:System.IDisposable", "<framework>", RelationKinds.Implements);

    [Fact]
    public void Hash_WhenOnlyPrivateMembersDiffer_IsEqual()
    {
        // Arrange
        DocumentExtraction without = new([Type, PublicMethod], [], []);
        DocumentExtraction with = new([Type, PublicMethod, PrivateField], [], []);

        // Act / Assert
        DeclarationSurface.Hash(with).Should().Be(DeclarationSurface.Hash(without));
    }

    [Fact]
    public void Hash_WhenPublicMemberAdded_Differs()
    {
        // Arrange
        DocumentExtraction before = new([Type], [], []);
        DocumentExtraction after = new([Type, PublicMethod], [], []);

        // Act / Assert
        DeclarationSurface.Hash(after).Should().NotBe(DeclarationSurface.Hash(before));
    }

    [Fact]
    public void Hash_WhenAccessibilityChanges_Differs()
    {
        // Arrange
        SymbolRecord internalMethod = PublicMethod with { Signature = "internal void M()", Accessibility = "internal" };

        // Act / Assert
        DeclarationSurface.Hash([Type, internalMethod], []).Should().NotBe(DeclarationSurface.Hash([Type, PublicMethod], []));
    }

    [Fact]
    public void Hash_WhenRelationChanges_Differs()
    {
        // Act / Assert
        DeclarationSurface.Hash([Type], [Implements]).Should().NotBe(DeclarationSurface.Hash([Type], []));
    }

    [Fact]
    public void Hash_IsIndependentOfRowOrder()
    {
        // Act / Assert
        DeclarationSurface.Hash([PublicMethod, Type], [Implements]).Should().Be(DeclarationSurface.Hash([Type, PublicMethod], [Implements]));
    }

    [Fact]
    public void Hash_WithNothingVisible_IsEmptyHash()
    {
        // Act / Assert
        DeclarationSurface.Hash(DocumentExtraction.Empty).Should().Be(DeclarationSurface.EmptyHash);
        DeclarationSurface.Hash([PrivateField], []).Should().Be(DeclarationSurface.EmptyHash);
    }
}
