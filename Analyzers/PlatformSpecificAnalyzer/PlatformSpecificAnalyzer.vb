﻿' FOR EASIER F5 DEBUGGING OF THIS ANALYZER:
' Set "PlatformSpecificAnalyzer" as your startup project. Then under MyProject > Debug, set
' StartAction: external program
'     C:\Program Files (x86)\Microsoft Visual Studio 14.0\Common7\IDE\devenv.exe
' Command-line arguments: 
'     "C:\Users\lwischik\Source\Repos\blog\Analyzers\DemoUWP_CS\DemoUWP_CS.sln" /RootSuffix Analyzer
'     "C:\Users\lwischik\Source\Repos\blog\Analyzers\DemoUWP_VB\DemoUWP_VB.sln" /RootSuffix Analyzer
' Note: I want to migrate this analyzer over to a PCL. But PCLs don't support the debug tab: https://github.com/dotnet/roslyn/issues/4542

Imports System.IO
Imports System.Runtime.CompilerServices
Imports Microsoft.CodeAnalysis

Public Enum PlatformKind
    Unchecked ' .NET and Win8.1
    Uwp ' the core UWP platform
    ExtensionSDK ' Desktop, Mobile, IOT, Xbox extension SDK
    User ' from when the user put a *Specific attribute on something
End Enum

Public Structure Platform
    Public Kind As PlatformKind
    Public Version As String ' For UWP, this is version 10240 or (placeholder) 10241. For User, the fully qualified name of the attribute in use

    Public Sub New(kind As PlatformKind, Optional version As String = Nothing)
        Me.Kind = kind
        Me.Version = version
        Select Case kind
            Case PlatformKind.Unchecked : If version IsNot Nothing Then Throw New ArgumentException("No version expected")
            Case PlatformKind.Uwp : If version <> "10240" AndAlso version <> "10241" Then Throw New ArgumentException("Only known SDKs are 10240 and 10241")
            Case PlatformKind.ExtensionSDK : If version IsNot Nothing Then Throw New ArgumentException("Don't specify versions for extension SDKs")
            Case PlatformKind.User : If Not version?.EndsWith("Specific") Then Throw New ArgumentException("User specific should end in Specific")
        End Select
    End Sub

    Public Shared Function OfSymbol(symbol As ISymbol) As Platform
        ' This function tells which version/platform the symbol is from.
        ' This function is hard-coded with knowledge up to SDK 10241.
        ' I could have made it a general-purpose function which looks up the SDK
        ' files on disk. But I think it's more elegant to hard-code it into the analyzer,
        ' so as to reduce disk-access while the analyzer runs.

        If symbol Is Nothing Then Return New Platform(PlatformKind.Unchecked)
        If symbol.ContainingNamespace?.ToDisplayString.StartsWith("Windows.") Then
            Dim assembly = symbol.ContainingAssembly.Name, version = symbol.ContainingAssembly.Identity.Version.Major

            ' Any call to ApiInformation.* is allowed without warning
            If symbol.ContainingType?.Name = "ApiInformation" Then Return New Platform(PlatformKind.Uwp, "10240")

            ' I don't want to give warning when analyzing code in an 8.1 or PCL project.
            ' In those two targets, every Windows type is found in Windows.winmd, so that's how we'll suppress it:
            If assembly = "Windows" Then Return New Platform(PlatformKind.Unchecked)

            ' Some WinRT types like Windows.UI.Color get projected to come from this assembly:
            If assembly = "System.Runtime.WindowsRuntime" Then Return New Platform(PlatformKind.Uwp, "10240")

            ' Some things are emphatically part of UWP.10240
            If assembly = "Windows.Foundation.FoundationContract" OrElse
                (assembly = "Windows.Foundation.UniversalApiContract" AndAlso version = 1) OrElse
                assembly = "Windows.Networking.Connectivity.WwanContract" Then Return New Platform(PlatformKind.Uwp, "10240")

            ' Some things were in platform-specific in 10240, but moved to UWP in 10241
            ' Should we report them as "platform-specific"? Or should we report them as "version-specific"?
            ' I'll report them as version-specific, because I think that will be a nicer message.
            If assembly = "Windows.ApplicationModel.Calls.CallsVoipContract" OrElse
               assembly = "Windows.Graphics.Printing3D.Printing3DContract" OrElse
               assembly = "Windows.Devices.Printers.PrintersContract" Then Return New Platform(PlatformKind.Uwp, "10241")

            ' Some things in UWP have been added between 10240 and 10241
            If assembly = "Windows.Foundation.UniversalApiContract" Then
                Dim d = GetUniversalApiAdditions()
                Dim isType = (symbol.Kind = SymbolKind.NamedType)
                Dim typeName = If(isType, symbol.ToDisplayString, symbol.ContainingType.ToDisplayString)
                Dim newMembers As List(Of String) = Nothing
                Dim in10241 = d.TryGetValue(typeName, newMembers)
                If Not in10241 Then Return New Platform(PlatformKind.Uwp, "10240") ' the type was in 10240
                If newMembers Is Nothing Then Return New Platform(PlatformKind.Uwp, "10241") ' the entire type was new in 10241
                If isType Then Return New Platform(PlatformKind.Uwp, "10240") ' the type was in 10240, even though members are new in 10241
                Dim memberName = symbol.Name
                If newMembers.Contains(memberName) Then Return New Platform(PlatformKind.Uwp, "10241") ' this member was new in 10241
                Return New Platform(PlatformKind.Uwp, "10240") ' this member existed in 10240
            End If

            ' All other Windows.* types come from platform-specific extensions
            Return New Platform(PlatformKind.ExtensionSDK)

        Else
            Dim attr = GetPlatformSpecificAttribute(symbol)
            If attr IsNot Nothing Then Return New Platform(PlatformKind.User, attr)
            Return New Platform(PlatformKind.Unchecked)
        End If
    End Function

End Structure


Class HowToGuard
    Public TypeToCheck As String
    Public MemberToCheck As String
    Public KindOfCheck As String = "IsTypePresent"
    Public AttributeToIntroduce As String = "System.Runtime.CompilerServices.PlatformSpecific"
    Public AttributeFriendlyName As String = "PlatformSpecific"

    Shared Function Symbol(target As ISymbol) As HowToGuard
        Dim plat = Platform.OfSymbol(target)
        '
        If plat.Kind = PlatformKind.Unchecked Then
            Throw New InvalidOperationException("oops! don't know why I was asked to check something that's fine")
        ElseIf plat.Kind = PlatformKind.User Then
            Dim lastDot = plat.Version.LastIndexOf("."c)
            Dim attrName = If(lastDot = -1, plat.Version, plat.Version.Substring(lastDot + 1))
            Return New HowToGuard With {.AttributeToIntroduce = plat.Version, .AttributeFriendlyName = attrName, .TypeToCheck = "??"}
        ElseIf plat.Kind = PlatformKind.ExtensionSDK Then
            Return New HowToGuard With {.TypeToCheck = If(target.Kind = SymbolKind.NamedType, target.ToDisplayString, target.ContainingType.ToDisplayString)}
        ElseIf plat.Kind = PlatformKind.Uwp AndAlso target.Kind = SymbolKind.NamedType Then
            Return New HowToGuard With {.TypeToCheck = target.ToDisplayString}
        ElseIf plat.Kind = PlatformKind.Uwp AndAlso target.Kind <> SymbolKind.NamedType Then
            Dim g As New HowToGuard With {.TypeToCheck = target.ContainingType.ToDisplayString}
            Dim d = GetUniversalApiAdditions(), newMembers As List(Of String) = Nothing
            If Not d.TryGetValue(g.TypeToCheck, newMembers) Then Throw New InvalidOperationException("oops! expected this UWP version API to be in the dictionary of new things")
            If newMembers IsNot Nothing Then
                g.MemberToCheck = target.Name
                If target.Kind = SymbolKind.Field Then g.KindOfCheck = "IsEnumNamedValuePresent" ' the only fields in WinRT are enum fields
                If target.Kind = SymbolKind.Event Then g.KindOfCheck = "IsEventPresent"
                If target.Kind = SymbolKind.Property Then g.KindOfCheck = "IsPropertyPresent" ' TODO: if SDK starts introducing additional accessors on properties, we'll have to change this
                If target.Kind = SymbolKind.Method Then g.KindOfCheck = "IsMethodPresent"
            End If
            Return g
        Else
            Throw New InvalidOperationException("oops! impossible platform kind")
        End If
    End Function

End Class


Module PlatformSpecificAnalyzer
    Public RulePlatform As New DiagnosticDescriptor("UWP001", "Platform-specific", "Platform-specific code", "Safety", DiagnosticSeverity.Warning, True)
    Public RuleVersion As New DiagnosticDescriptor("UWP002", "Version-specific", "Version-specific code", "Safety", DiagnosticSeverity.Warning, True)

    Function GetPlatformSpecificAttribute(symbol As ISymbol) As String
        If symbol Is Nothing Then Return Nothing
        For Each attr In symbol.GetAttributes
            If attr.AttributeClass.Name.EndsWith("SpecificAttribute") Then Return attr.AttributeClass.ToDisplayString.Replace("Attribute", "")
        Next
        Return Nothing
    End Function

    Function HasPlatformSpecificAttribute(symbol As ISymbol) As Boolean
        Return (GetPlatformSpecificAttribute(symbol) IsNot Nothing)
    End Function


    Function GetUniversalApiAdditions() As Dictionary(Of String, List(Of String))
        ' I don't yet know what the new Windows SDK will be like, nor what new types it will add.
        ' As a placeholder, to test my code, I'm going to pretend that these two APIs from the existing SDK are actually new...
        Static Dim _d As New Dictionary(Of String, List(Of String)) From {
            {"Windows.ApplicationModel.Activation.SplashScreen", "ImageLocation"},
            {"Windows.ApplicationModel.Activation.ActivationKind"}
                }
        Return _d
    End Function


    <Extension>
    Sub Add(d As Dictionary(Of String, List(Of String)), type As String, ParamArray members As String())
        If members.Length = 0 Then d.Add(type, Nothing) Else d.Add(type, members.ToList())
    End Sub

End Module

Class TargetPlatformMinVersion
    Private projfile As String
    Private version As String
    Private lastCheck As DateTime
    Private lastWriteTime As DateTime
    Private watcher As FileSystemWatcher

    Public Shared Function [Get](comp As Compilation, tree As SyntaxTree, ext As String) As String
        ' Hack: because of https://github.com/dotnet/roslyn/issues/6627 it's impossible
        ' to get information from the csproj in an analyzer. So I'm going to hack around it...
        ' I've heard bad things about FileSystemWatcher, so in addition to receiving its notifications, I'm also
        ' going to poll (capped at once every 30 seconds)

        ' Note that this first TryGetValue path doesn't hit the filesystem.
        Dim dir = Path.GetDirectoryName(tree.FilePath)
        Dim cacheKey = $"{dir}\{comp.AssemblyName}{ext}"
        Static Dim cache As New Dictionary(Of String, TargetPlatformMinVersion)
        Dim entry As TargetPlatformMinVersion = Nothing
        If cache.TryGetValue(cacheKey, entry) AndAlso entry.lastCheck + TimeSpan.FromSeconds(30) > DateTime.Now Then Return entry.version

        If entry Is Nothing Then entry = New TargetPlatformMinVersion With {.lastCheck = DateTime.Now} : cache.Add(cacheKey, entry)

        ' We don't have a reliable way to get the project file. So I'm going to hack it.
        ' projFile is the key that's used for the TryGet dictionary, while fn is our
        ' best guess as to the actual location of the proj file.
        If entry.projfile Is Nothing Then
            entry.projfile = cacheKey
            If Not File.Exists(entry.projfile) Then dir = Path.GetDirectoryName(dir) : entry.projfile = $"{dir}\{comp.AssemblyName}{ext}"
            If Not File.Exists(entry.projfile) Then dir = Path.GetDirectoryName(dir) : entry.projfile = $"{dir}\{comp.AssemblyName}{ext}"
        End If
        If Not File.Exists(entry.projfile) Then entry.version = Nothing : Return entry.version

        ' Set up a file watcher
        If entry.watcher Is Nothing Then
            entry.watcher = New FileSystemWatcher With {.Path = Path.GetDirectoryName(entry.projfile), .Filter = Path.GetFileName(entry.projfile), .NotifyFilter = NotifyFilters.LastWrite, .EnableRaisingEvents = True}
            AddHandler entry.watcher.Changed, Sub() entry.lastCheck = DateTime.Now - TimeSpan.FromSeconds(30)
        End If

        ' But our primary mechanism is checking the file on-demand (throttled above to once every 30s, or once each proj-file-save, whichever is sooner)
        Dim lastWriteTime = File.GetLastWriteTime(entry.projfile) : If entry.lastWriteTime = lastWriteTime Then Return entry.version
        Dim lines As String() = Nothing
        Try
            lines = File.ReadAllLines(entry.projfile)
        Catch ex As IOException
            Return entry.version
        End Try
        Dim line = lines.FirstOrDefault(Function(s) s.Trim.StartsWith("<TargetPlatformMinVersion>"))
        entry.version = line?.Replace("<TargetPlatformMinVersion>", "").Replace("</TargetPlatformMinVersion>", "").Replace("</>", "").Replace("10.0.", "").Replace(".0", "").Trim()
        entry.lastWriteTime = lastWriteTime
        entry.lastCheck = DateTime.Now
        Return entry.version
    End Function
End Class
