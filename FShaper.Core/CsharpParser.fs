﻿// C# parser - this walks the CSharp syntax tree and creates the intermediate F# syntax tree
namespace FShaper.Core

open System
open FSharp.Compiler.SyntaxTree
open FShaper.Core
open Fantomas.TriviaTypes
open Microsoft.CodeAnalysis
open System.Linq
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open FSharp.Compiler
open FSharp.Compiler.Range
open System.Threading
        
[<AutoOpen>]
module SimpleFormatter = 

    type Config = Debug | Release

    let config = Debug

    let debugFormat s f : Expr = 
        match config with 
        | Debug -> s |> toLongIdent
        | Release -> f () 

type CSharpStatementWalker() = 

    static member ToCsharpSyntaxNode (node:SyntaxNode) =
        match node with
        | :? CSharpSyntaxNode as x -> Some x
        | _ -> None

    static member ParseLeftAssignmentExpressionSyntax right (node:AssignmentExpressionSyntax) = 
        match node.Left with 
        | :? ElementAccessExpressionSyntax as x -> 
            let e = CSharpStatementWalker.ParseExpression x.Expression

            x.ArgumentList.Arguments 
            |> Seq.map CSharpStatementWalker.ParseCsharpNode
            |> Seq.toList
            |> function 
            | [x] -> 

                match containsExpr 
                    (function | Expr.LongIdentSet (l, _) -> true | _ -> false) x with 
                | [] -> Expr.DotIndexedSet(e, [IndexerArg.One (x)], right)
                | Expr.LongIdentSet (var, expr)::_ -> 

                    let x = 
                        replaceExpr 
                            (function 
                                | Expr.LongIdentSet (v, _) when joinLongIdentWithDots v = joinLongIdentWithDots var -> Expr.LongIdent (false, var) |> Some
                                | _ -> None ) x

                    Expr.Sequential (false, Expr.LongIdentSet (var, expr), Expr.DotIndexedSet(e, [IndexerArg.One (x)], right))
                | _ -> Expr.DotIndexedSet(e, [IndexerArg.One (x)], right)

                //let walker tree = 
                //match x with 
                //| Expr.LongIdentSet (l, _) as x -> 
                    //Expr.Sequential (SequencePointsAtSeq, false, x, Expr.DotIndexedSet(e, [IndexerArg.One (Expr.LongIdent (false, l))], right))
                
            | _ -> 
                let ident = createErrorCode "ElementAccessExpressionSyntax" node
                Expr.LongIdent (false, ident)                
        | _ -> 
            let l = LongIdentWithDots (node.Left.WithoutTrivia().ToFullString() |> toIdent, [range0])
            Expr.LongIdentSet (l, right)
    

    static member ParseAssignmentExpressionSyntax (node:AssignmentExpressionSyntax): Expr =
        match node.Kind() with

        | SyntaxKind.SubtractAssignmentExpression -> 

            let left = CSharpStatementWalker.ParseLeftAssignmentExpressionSyntax Expr.InLetPlaceholder node
            let right = 
                let r = CSharpStatementWalker.ParseCsharpNode node.Right  
                let isMinusEquals = node.ChildTokens() |> Seq.exists (fun x -> x.Kind() = SyntaxKind.MinusEqualsToken)
                if isMinusEquals then 
                    match r with 
                    | Expr.Lambda _ -> 

                        // TODO: This needs soem work to unassign an event handler. 
                        let ident = createErrorCode "SubtractAssignmentExpression right" node
                        Expr.LongIdent (false, ident)
                        //let ident = node.Left.WithoutTrivia().ToFullString() |> toLongIdent
                        //let app = Expr.DotGet (ident, toLongIdentWithDots "AddHandler" )
                        //let app = Expr.TypeApp (app, [SynType.Anon range0])
                        //Expr.App (ExprAtomicFlag.Atomic, false, app, Expr.Paren right)
                    | _ -> 
                        let addOp = PrettyNaming.CompileOpName "-" |> Expr.Ident
                        ExprOps.toInfixApp (identSetToIdentGet left) addOp r
                else r

            match left with 
            | Expr.LongIdentSet (l, Expr.InLetPlaceholder) -> Expr.LongIdentSet (l, right)
            | Expr.DotIndexedSet(e, [IndexerArg.One (x)], Expr.InLetPlaceholder) -> Expr.DotIndexedSet(e, [IndexerArg.One (x)], right)
            | Expr.Sequential (false, Expr.LongIdentSet (var, expr), Expr.DotIndexedSet(e, [IndexerArg.One (x)], Expr.InLetPlaceholder)) -> 
                Expr.Sequential (false, Expr.LongIdentSet (var, expr), Expr.DotIndexedSet(e, [IndexerArg.One (x)], right))
            | _ -> 
                let ident = createErrorCode "SubtractAssignmentExpression left" node
                Expr.LongIdent (false, ident)

        | SyntaxKind.SimpleAssignmentExpression -> 
            let right = node.Right |> CSharpStatementWalker.ParseExpression
            CSharpStatementWalker.ParseLeftAssignmentExpressionSyntax right node

        | SyntaxKind.AddAssignmentExpression ->
            
            let isPlusEquals = 
                node.ChildTokens() |> Seq.exists (fun x -> x.Kind() = SyntaxKind.PlusEqualsToken)

            let left = 
                node.Left |> CSharpStatementWalker.ParseCsharpNode
            let right = node.Right |> CSharpStatementWalker.ParseCsharpNode

            if isPlusEquals then 
                match right with 
                | Expr.Lambda _ -> 

                    let ident = node.Left.WithoutTrivia().ToFullString() |> toLongIdent
                    let app = Expr.DotGet (ident, toLongIdentWithDots "AddHandler" )
                    let app = Expr.TypeApp (app, [SynType.Anon range0])
                    Expr.App (ExprAtomicFlag.Atomic, false, app, Expr.Paren right)
                | _ -> 
                    let addOp = PrettyNaming.CompileOpName "+"
                    let add = ExprOps.toInfixApp left (Expr.Ident addOp) right
                    Expr.Set (left, add)
            else
                Expr.Set (left, right)


        | _ -> node.WithoutTrivia().ToFullString() |> Expr.Ident

    static member ParseBinaryExpression (node:BinaryExpressionSyntax):Expr = 


        let parseLeftAndRight () = 
            let left = 
                match node.Left with 
                | :? BinaryExpressionSyntax as x -> CSharpStatementWalker.ParseBinaryExpression x 
                | _ -> CSharpStatementWalker.ParseExpressionWithVariables node.Left

            let right = 
                match node.Right with 
                | :? BinaryExpressionSyntax as x -> CSharpStatementWalker.ParseBinaryExpression x 
                | _ -> CSharpStatementWalker.ParseCsharpNode node.Right

            left,right            

        let createLogicalExpression join = 
            let (left, right) = parseLeftAndRight ()

            let right = 
                match join, left with
                | "op_Addition", Expr.Const (SynConst.String (_,_)) -> 

                    //let right = 
                    match right with 
                    | FindIdent xs -> 
                        match xs with 
                        | [(s,_)] -> Expr.App(ExprAtomicFlag.NonAtomic, false, sprintf "%s.ToString" s |> toLongIdent, Expr.Const SynConst.Unit) |> Expr.Paren
                        | _ -> right
                    | _ -> right
                | _, _ -> right

            ExprOps.toInfixApp left (Expr.Ident join) right

        match node.Kind() with 
        | SyntaxKind.AddExpression -> PrettyNaming.CompileOpName "+" |> createLogicalExpression
        | SyntaxKind.MultiplyExpression -> PrettyNaming.CompileOpName "*" |> createLogicalExpression
        | SyntaxKind.DivideExpression -> PrettyNaming.CompileOpName "/" |> createLogicalExpression
        | SyntaxKind.SubtractExpression -> PrettyNaming.CompileOpName "-" |> createLogicalExpression
        | SyntaxKind.LogicalAndExpression -> PrettyNaming.CompileOpName "&&" |> createLogicalExpression
        | SyntaxKind.LogicalOrExpression -> PrettyNaming.CompileOpName "||" |> createLogicalExpression 
        | SyntaxKind.NotEqualsExpression -> PrettyNaming.CompileOpName "<>" |> createLogicalExpression 
        | SyntaxKind.EqualsExpression -> PrettyNaming.CompileOpName "=" |> createLogicalExpression 
        | SyntaxKind.GreaterThanOrEqualExpression -> PrettyNaming.CompileOpName ">=" |> createLogicalExpression 
        | SyntaxKind.GreaterThanExpression -> PrettyNaming.CompileOpName ">" |> createLogicalExpression 
        | SyntaxKind.LessThanExpression -> PrettyNaming.CompileOpName "<" |> createLogicalExpression 
        | SyntaxKind.LessThanOrEqualExpression -> PrettyNaming.CompileOpName "<=" |> createLogicalExpression 
        | SyntaxKind.ModuloExpression -> PrettyNaming.CompileOpName "%" |> createLogicalExpression
        | SyntaxKind.BitwiseOrExpression -> 
            PrettyNaming.CompileOpName "|||" |> createLogicalExpression 
            |> Expr.Paren // This seems to be required for Android as flags are used in value assignment in a class Attribute.
        | SyntaxKind.IsExpression -> 
            let t = (node.Right :?> TypeSyntax) |> parseType
            Expr.TypeTest (node.Left |> CSharpStatementWalker.ParseExpression, t)

        | SyntaxKind.AsExpression -> 

            let left = 
                match node.Left with 
                | _ -> CSharpStatementWalker.ParseExpression node.Left

            let right = 
                match node.Right with 
                | :? IdentifierNameSyntax as x -> x.Identifier.ValueText |> toLongIdentWithDots |>  SynType.LongIdent
                | x -> sprintf "Expected type identifier, but got: %A" x |> failwith

            Expr.Downcast (left, right)
        | SyntaxKind.CoalesceExpression -> 

            // There are two cases to handle here. 
            // 1) assigment. This is the simple case `x ?? (x = 42)` and means that we can translate to a simple `if x = null then ...; x`
            // 2) setting a default value. This is actually harder since it can be nested in other statements. An if then 
            // actually requires two statements so won't be correct. Instead we use the Option and Option.defaultValue which can all be done 
            // in a single line. 

            let (left, right) = parseLeftAndRight ()
            let x = right |> containsExpr (function 
                | Expr.LongIdentSet _
                | Expr.Set _ -> true
                | _ -> false  )

            if x |> List.isEmpty then
                // For case 2, the expression must be wrapped in parens to ensure the arguments are applied correctly
                let right = Expr.Paren right
            
                let pipe = PrettyNaming.CompileOpName "|>" |> toLongIdent
                let defaultValue = ExprOps.toApp (toLongIdent "Option.defaultValue") right
                let toOptional = ExprOps.toInfixApp left pipe (toLongIdent "Option.ofObj")
                ExprOps.toInfixApp toOptional pipe defaultValue 
            else             
                let isNull = ExprOps.toInfixApp left ("=" |> PrettyNaming.CompileOpName |> Expr.Ident) Expr.Null
                let assign = Expr.IfThenElse (isNull, right, None, false)
                sequential [assign; left]

        | e -> 
            let ident = createErrorCode "ParseBinaryExpresson" node
            Expr.LongIdent (false, ident)

    static member ParseToken (node:SyntaxToken) = 

        match node.Kind() with 
        | SyntaxKind.MinusToken -> "-" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.ExclamationToken -> "!" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.EqualsGreaterThanToken -> ">=" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.EqualsToken -> "=" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.GreaterThanToken -> ">" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.LessThanToken -> "<" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.LessThanEqualsToken -> "<=" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.SemicolonToken-> ";" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.NotEqualsExpression-> "!=" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.NumericLiteralToken -> 

            let text = node.WithoutTrivia().Text
            let lowerText = text.ToLower()

            let tryParseHexToInt (s: string) =
                if s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) 
                then Int32.TryParse(s.[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)
                else Int32.TryParse s

            let asInt = 
                // added for hex values to pass tests in enums
                // SysConst args are of that type, numeric base doesn't seem to be preservable
                match lowerText |> tryParseHexToInt with 
                | true, x -> Some x 
                | false,_ -> None

            let asInt64 = 
                match lowerText.Replace("l","") |> Int64.TryParse  with 
                | true, x -> Some x 
                | false,_ ->  None 

            let asFloat = 
                match lowerText.Replace(".","") |> Double.TryParse with 
                | true, x -> Some x 
                | false,_ ->  None 

            match asInt, asInt64, asFloat with 
            | Some x, _, _ ->  Expr.Const <| SynConst.Int32 x
            | _, Some x, _ ->  Expr.Const <| SynConst.Int64 x
            | _, _, Some x ->  Expr.Const <| SynConst.Double x
            | _, _, _  ->  toLongIdent text

        | SyntaxKind.IdentifierToken -> node.WithoutTrivia().ToFullString() |> toLongIdent
        //| SyntaxKind.ReturnKeyword -> Expr.Const SynConst.Unit
        //| SyntaxKind.ForEachKeyword -> "for" |> toLongIdent

        | SyntaxKind.BoolKeyword -> "Bool" |> toLongIdent
        | SyntaxKind.ByteKeyword -> "Byte" |> toLongIdent
        | SyntaxKind.SByteKeyword -> "SByte" |> toLongIdent
        | SyntaxKind.ShortKeyword -> "Short" |> toLongIdent
        | SyntaxKind.UShortKeyword -> "UShort" |> toLongIdent
        | SyntaxKind.IntKeyword -> "Int" |> toLongIdent
        | SyntaxKind.UIntKeyword -> "UInt" |> toLongIdent
        | SyntaxKind.LongKeyword -> "Long" |> toLongIdent
        | SyntaxKind.ULongKeyword -> "ULong" |> toLongIdent
        | SyntaxKind.DoubleKeyword -> "Double" |> toLongIdent
        | SyntaxKind.FloatKeyword -> "Float" |> toLongIdent
        | SyntaxKind.DecimalKeyword -> "Decimal" |> toLongIdent
        | SyntaxKind.StringKeyword -> "String" |> toLongIdent
        | SyntaxKind.CharKeyword -> "Char" |> toLongIdent
        | SyntaxKind.VoidKeyword -> "Unit" |> toLongIdent
        | SyntaxKind.ObjectKeyword -> "obj" |> toLongIdent
        | SyntaxKind.TypeOfKeyword -> "TypeOf" |> toLongIdent
        | SyntaxKind.SizeOfKeyword -> "SizeOf" |> toLongIdent
        | SyntaxKind.NullKeyword -> Expr.Null
        | SyntaxKind.TrueKeyword -> SynConst.Bool true |> Expr.Const 
        | SyntaxKind.FalseKeyword -> SynConst.Bool false |> Expr.Const
        | SyntaxKind.IfKeyword -> "If" |> toLongIdent
        | SyntaxKind.ElseKeyword -> "Else" |> toLongIdent
        | SyntaxKind.WhileKeyword -> "While" |> toLongIdent
        | SyntaxKind.ForKeyword -> "For" |> toLongIdent
        | SyntaxKind.ForEachKeyword -> "ForEach" |> toLongIdent
        | SyntaxKind.DoKeyword -> "Do" |> toLongIdent
        | SyntaxKind.SwitchKeyword -> "Switch" |> toLongIdent
        | SyntaxKind.CaseKeyword -> "Case" |> toLongIdent
        | SyntaxKind.DefaultKeyword -> "Default" |> toLongIdent
        | SyntaxKind.TryKeyword -> "Try" |> toLongIdent
        | SyntaxKind.CatchKeyword -> "Catch" |> toLongIdent
        | SyntaxKind.FinallyKeyword -> "Finally" |> toLongIdent
        | SyntaxKind.LockKeyword -> "Lock" |> toLongIdent
        | SyntaxKind.GotoKeyword -> "Goto" |> toLongIdent
        | SyntaxKind.BreakKeyword -> "Break" |> toLongIdent
        | SyntaxKind.ContinueKeyword -> "Continue" |> toLongIdent
        //| SyntaxKind.ReturnKeyword -> Expr.ReturnFromIf
        | SyntaxKind.ThrowKeyword -> "Throw" |> toLongIdent
        | SyntaxKind.PublicKeyword -> "Public" |> toLongIdent
        | SyntaxKind.PrivateKeyword -> "Private" |> toLongIdent
        | SyntaxKind.InternalKeyword -> "Internal" |> toLongIdent
        | SyntaxKind.ProtectedKeyword -> "Protected" |> toLongIdent
        | SyntaxKind.StaticKeyword -> "Static" |> toLongIdent
        | SyntaxKind.ReadOnlyKeyword -> "ReadOnly" |> toLongIdent
        | SyntaxKind.SealedKeyword -> "Sealed" |> toLongIdent
        | SyntaxKind.ConstKeyword -> "Const" |> toLongIdent
        | SyntaxKind.FixedKeyword -> "Fixed" |> toLongIdent
        | SyntaxKind.StackAllocKeyword -> "StackAlloc" |> toLongIdent
        | SyntaxKind.VolatileKeyword -> "Volatile" |> toLongIdent
        | SyntaxKind.NewKeyword -> "New" |> toLongIdent
        | SyntaxKind.OverrideKeyword -> "Override" |> toLongIdent
        | SyntaxKind.AbstractKeyword -> "Abstract" |> toLongIdent
        | SyntaxKind.VirtualKeyword -> "Virtual" |> toLongIdent
        | SyntaxKind.EventKeyword -> "Event" |> toLongIdent
        | SyntaxKind.ExternKeyword -> "Extern" |> toLongIdent
        | SyntaxKind.RefKeyword -> "Ref" |> toLongIdent
        | SyntaxKind.OutKeyword -> "Out" |> toLongIdent
        | SyntaxKind.InKeyword -> "In" |> toLongIdent
        | SyntaxKind.IsKeyword -> "Is" |> toLongIdent
        | SyntaxKind.AsKeyword -> "As" |> toLongIdent
        | SyntaxKind.ParamsKeyword -> "Params" |> toLongIdent
        | SyntaxKind.ArgListKeyword -> "ArgList" |> toLongIdent
        | SyntaxKind.MakeRefKeyword -> "MakeRef" |> toLongIdent
        | SyntaxKind.RefTypeKeyword -> "RefType" |> toLongIdent
        | SyntaxKind.RefValueKeyword -> "RefValue" |> toLongIdent
        | SyntaxKind.ThisKeyword -> "This" |> toLongIdent
        | SyntaxKind.BaseKeyword -> "Base" |> toLongIdent
        | SyntaxKind.NamespaceKeyword -> "Namespace" |> toLongIdent
        //| SyntaxKind.UsingKeyword -> "Using" |> toLongIdent
        | SyntaxKind.ClassKeyword -> "Class" |> toLongIdent
        | SyntaxKind.StructKeyword -> "Struct" |> toLongIdent
        | SyntaxKind.InterfaceKeyword -> "Interface" |> toLongIdent
        | SyntaxKind.EnumKeyword -> "Enum" |> toLongIdent
        | SyntaxKind.DelegateKeyword -> "Delegate" |> toLongIdent
        | SyntaxKind.CheckedKeyword -> "Checked" |> toLongIdent
        | SyntaxKind.UncheckedKeyword -> "Unchecked" |> toLongIdent
        | SyntaxKind.UnsafeKeyword -> "Unsafe" |> toLongIdent
        | SyntaxKind.OperatorKeyword -> "Operator" |> toLongIdent
        | SyntaxKind.ExplicitKeyword -> "Explicit" |> toLongIdent
        | SyntaxKind.ImplicitKeyword -> "Implicit" |> toLongIdent
        | SyntaxKind.YieldKeyword -> "Yield" |> toLongIdent
        | SyntaxKind.PartialKeyword -> "Partial" |> toLongIdent
        | SyntaxKind.AliasKeyword -> "Alias" |> toLongIdent
        | SyntaxKind.GlobalKeyword -> "Global" |> toLongIdent
        | SyntaxKind.AssemblyKeyword -> "Assembly" |> toLongIdent
        | SyntaxKind.ModuleKeyword -> "Module" |> toLongIdent
        | SyntaxKind.TypeKeyword -> "Type" |> toLongIdent
        | SyntaxKind.FieldKeyword -> "Field" |> toLongIdent
        | SyntaxKind.MethodKeyword -> "Method" |> toLongIdent
        | SyntaxKind.ParamKeyword -> "Param" |> toLongIdent
        | SyntaxKind.PropertyKeyword -> "Property" |> toLongIdent
        | SyntaxKind.TypeVarKeyword -> "TypeVar" |> toLongIdent
        | SyntaxKind.GetKeyword -> "Get" |> toLongIdent
        | SyntaxKind.SetKeyword -> "Set" |> toLongIdent
        | SyntaxKind.AddKeyword -> "Add" |> toLongIdent
        | SyntaxKind.RemoveKeyword -> "Remove" |> toLongIdent
        | SyntaxKind.WhereKeyword -> "Where" |> toLongIdent
        | SyntaxKind.FromKeyword -> "From" |> toLongIdent
        | SyntaxKind.GroupKeyword -> "Group" |> toLongIdent
        | SyntaxKind.JoinKeyword -> "Join" |> toLongIdent
        | SyntaxKind.IntoKeyword -> "Into" |> toLongIdent
        | SyntaxKind.LetKeyword -> "Let" |> toLongIdent
        | SyntaxKind.ByKeyword -> "By" |> toLongIdent
        | SyntaxKind.SelectKeyword -> "Select" |> toLongIdent
        | SyntaxKind.OrderByKeyword -> "OrderBy" |> toLongIdent
        | SyntaxKind.OnKeyword -> "On" |> toLongIdent
        | SyntaxKind.EqualsKeyword -> "Equals" |> toLongIdent
        | SyntaxKind.AscendingKeyword -> "Ascending" |> toLongIdent
        | SyntaxKind.DescendingKeyword -> "Descending" |> toLongIdent
        | SyntaxKind.NameOfKeyword -> "NameOf" |> toLongIdent
        | SyntaxKind.AsyncKeyword -> "Async" |> toLongIdent
        | SyntaxKind.AwaitKeyword -> "Await" |> toLongIdent
        | SyntaxKind.WhenKeyword -> "When" |> toLongIdent
        | SyntaxKind.ElifKeyword -> "Elif" |> toLongIdent
        | SyntaxKind.EndIfKeyword -> "EndIf" |> toLongIdent
        | SyntaxKind.RegionKeyword -> "Region" |> toLongIdent
        | SyntaxKind.EndRegionKeyword -> "EndRegion" |> toLongIdent
        | SyntaxKind.DefineKeyword -> "Define" |> toLongIdent
        | SyntaxKind.UndefKeyword -> "Undef" |> toLongIdent
        | SyntaxKind.WarningKeyword -> "Warning" |> toLongIdent
        | SyntaxKind.ErrorKeyword -> "Error" |> toLongIdent
        | SyntaxKind.LineKeyword -> "Line" |> toLongIdent
        | SyntaxKind.PragmaKeyword -> "Pragma" |> toLongIdent
        | SyntaxKind.HiddenKeyword -> "Hidden" |> toLongIdent
        | SyntaxKind.ChecksumKeyword -> "Checksum" |> toLongIdent
        | SyntaxKind.DisableKeyword -> "Disable" |> toLongIdent
        | SyntaxKind.RestoreKeyword -> "Restore" |> toLongIdent
        | SyntaxKind.ReferenceKeyword -> "Reference" |> toLongIdent
        | SyntaxKind.LoadKeyword -> "Load" |> toLongIdent
        | SyntaxKind.PlusPlusToken -> "++" |> toLongIdent
        | SyntaxKind.MinusMinusToken -> "--" |> toLongIdent
        | SyntaxKind.StringLiteralToken -> (node.ValueText, range0) |> SynConst.String |> Expr.Const 
        | SyntaxKind.CharacterLiteralToken -> (node.Value :?> Char) |> SynConst.Char |> Expr.Const 
        | _ -> 
            let ident = createErrorCode "ParseToken" node.Parent
            Expr.LongIdent (false, ident)

            //| SyntaxKind.CharacterLiteralToken -> Line ""
            //| _ -> node.ValueText |> Line

    static member ParseInterpolatedStringContentSyntax (node:InterpolatedStringContentSyntax) = 
        match node with 
        //| :? InterpolatedStringTextSyntax as x -> "-" + x.TextToken.Text |> Line
        | :? InterpolationSyntax as x -> x.Expression |> CSharpStatementWalker.ParseExpression |> Expr.Paren
        | :? InterpolatedStringTextSyntax as x -> Expr.Const (SynConst.String (x.WithoutTrivia().ToString(), range0))
        | e -> 
            let ident = createErrorCode "ParseInterpolatedStringContentSyntax" node
            Expr.LongIdent (false, ident)

    static member ParseStatementSyntax (node:StatementSyntax) =
        let result = 
            match node with 
            | :? BlockSyntax as x -> x.Statements  |> Seq.map CSharpStatementWalker.ParseStatementSyntax |> sequential
            | :? BreakStatementSyntax as x -> Expr.ReturnFromIf <| Expr.Const SynConst.Unit // "BreakStatement" |> toLongIdent
            //| :? CheckedStatementSyntax as x -> "CheckedStatement" |> toLongIdent
            | :? CommonForEachStatementSyntax as x -> 
                match x with 
                | :? ForEachStatementSyntax as x -> 

                    let var = Pat.Named (Pat.Wild, Ident(x.Identifier.ValueText, range0), false, None)
                    let exp = x.Expression |> CSharpStatementWalker.ParseCsharpNode
                    let body = x.Statement |> CSharpStatementWalker.ParseCsharpNode
                    Expr.ForEach (SeqExprOnly.SeqExprOnly false, true, var, exp, body)

                | :? ForEachVariableStatementSyntax as x -> 

                    let var = x.Variable |> CSharpStatementWalker.ParseCsharpNode |> ParserUtil.parseVariableName    
                    let exp = x.Expression |> CSharpStatementWalker.ParseCsharpNode
                    let body = x.Statement |> CSharpStatementWalker.ParseCsharpNode

                    Expr.ForEach (SeqExprOnly.SeqExprOnly false, true, var, exp, body)
                | e -> 
                    let ident = createErrorCode "ParseStatementSyntax" node
                    Expr.LongIdent (false, ident)

            | :? ContinueStatementSyntax as x -> Expr.ReturnFromIf <| Expr.Const SynConst.Unit
            //| :? DoStatementSyntax as x -> "DoStatement" |> toLongIdent
            | :? EmptyStatementSyntax as x -> Expr.Const SynConst.Unit
            | :? ExpressionStatementSyntax as x -> x.Expression |> CSharpStatementWalker.ParseExpressionWithVariables
            //| :? FixedStatementSyntax as x -> "FixedStatement" |> toLongIdent
            | :? ForStatementSyntax as x -> 

                let replaceCastToInt node = 
                    node 
                    |> replaceExpr  
                        (function 
                            | Expr.Downcast (e, _) -> 
                                ExprOps.toApp (toLongIdent "int") e |> Some
                            | _ -> None)

                let varAndStartValue = 
                    x.Declaration 
                    |> Option.ofObj 
                    |> Option.map (fun x -> x.Variables |> Seq.toList) 
                    |> Option.toList
                    |> List.concat
                    |> function 
                    | [x] -> 
                        let var = 
                            match CSharpStatementWalker.ParseToken x.Identifier with 
                            | Expr.Ident x -> toSingleIdent x |> Some
                            | Expr.LongIdent (_, s) -> s |> joinLongIdentWithDots |> toSingleIdent |> Some
                            | e -> None

                        let init = 
                            CSharpStatementWalker.ParseExpression x.Initializer.Value |> replaceCastToInt

                        match var with 
                        | Some a  -> Some (a,init)
                        | _ -> None
                    | _ -> None

                let endValue = 
                    let validOps = ["op_LessThan"; "op_LessThanOrEqual"; "op_LessThanOrEqual"; "op_GreaterThanOrEqual"; "op_GreaterThan"]
                    match CSharpStatementWalker.ParseExpression x.Condition with
                    | Expr.Const _ as e -> Some e
                    | BinaryOp (left,"op_LessThan",Expr.Const (SynConst.Int32 a)) -> Expr.Const (SynConst.Int32 (a - 1)) |> Some
                    | BinaryOp (left,"op_LessThan",right) -> 
                        let subtract = "-" |> PrettyNaming.CompileOpName |> toLongIdent
                        Expr.Paren (ExprOps.toInfixApp right subtract (Expr.Const (SynConst.Int32 1)) ) |> Some
                    | BinaryOp (left,"op_GreaterThan",Expr.Const (SynConst.Int32 a)) -> 
                        a + 1 |> SynConst.Int32 |> Expr.Const |> Some // Add one since it is >
                    | BinaryOp (left,op, a) when List.contains op validOps -> a |> replaceCastToInt |> Some
                    | e -> printfn "END: %A" e; None

                let isIncrement = 
                    x.Incrementors 
                    |> Seq.toList 
                    |> List.choose (fun x -> 
                        let a = 
                            match x with 
                            | IsPostFixIncrement -> Some true
                            | IsPostFixDecrement -> Some false
                            | IsNotPostFix -> None 
                        let b = 
                            match x with 
                            | IsPreFixIncrement -> Some true
                            | IsPreFixDecrement -> Some false
                            | IsNotPreFix -> None 
                        match a, b with 
                        | Some _, Some _ -> a
                        | _, Some _ -> b
                        | Some _, _ -> a
                        | None, None  -> None )
                    |> List.tryHead

                let statement = 
                    let e = CSharpStatementWalker.ParseCsharpNode x.Statement //|> replaceAnyPostOrPreIncrement
                    x.Incrementors 
                    |> Seq.toList 
                    |> List.choose (fun x -> 
                        let a = 
                            match x with 
                            | IsPostFixIncrement -> None
                            | IsPostFixDecrement -> None
                            | IsNotPostFix -> Some x  
                            
                        let b = 
                            match x with 
                            | IsPreFixIncrement -> None
                            | IsPreFixDecrement -> None
                            | IsNotPreFix -> Some x 
                        match a, b with 
                        | Some _, Some _ -> a
                        | _, _  -> None   )
                    |> List.map (CSharpStatementWalker.ParseExpression)
                    |> function
                    | [] -> e
                    | xs -> (e :: xs) |> sequential
                    
                match varAndStartValue, endValue, isIncrement with 
                | Some (var, start), Some endValue, Some isIncrement -> 
                    Expr.For (var, start, isIncrement, endValue, statement)
                | None, Some endValue, Some isIncrement ->     

                    let validOps = ["op_LessThan"; "op_LessThanOrEqual"; "op_LessThanOrEqual"; "op_GreaterThanOrEqual"; "op_GreaterThan"]
                    let leftConditionValues = 
                        match CSharpStatementWalker.ParseExpression x.Condition with
                        | BinaryOp (Expr.LongIdent (_,a),op, _) when List.contains op validOps -> Some a
                        | BinaryOp (Expr.LongIdent (_,a),op, _) when List.contains op validOps -> Some a
                        | BinaryOp (Expr.LongIdent (_,a),op, _) when List.contains op validOps -> Some a
                        | e -> printfn "END: %A" e; None

                    match leftConditionValues with 
                    | Some var -> 

                        let initializers = 
                            x.Initializers 
                            |> Seq.toList 
                            |> List.map CSharpStatementWalker.ParseExpressionWithVariables

                        let start = 
                            let var = joinLongIdentWithDots var
                            let xs = 
                                initializers
                                |> List.choose (function 
                                    | FindIdent xs -> Some xs
                                    | _  ->  None)
                                |> List.concat

                            printfn "%A" initializers

                            xs
                            |> function 
                            | [] -> failwith ""
                            | xs -> 
                                match xs |> List.filter (fun (x,_) -> x = var) with 
                                | [] -> failwith ""
                                | xs -> 
                                    match xs |> List.head |> snd with 
                                    | Some x -> x
                                    | None -> failwith ""
                                
                        let f = Expr.For (var |> joinLongIdentWithDots |> toSingleIdent, start, isIncrement, endValue, statement)             
                        (initializers @ [f]) |> sequential
                            
                    | _ -> 
                        let ident = createErrorCode "ForStatementSyntax leftConditionValues" node
                        Expr.LongIdent (false, ident)

                | _, _, _ -> 
                    let ident = createErrorCode "ForStatementSyntax varAndStartValue, endValue, isIncrement" node
                    Expr.LongIdent (false, ident)

            //| :? GotoStatementSyntax as x -> "GotoStatement" |> toLongIdent
            | :? IfStatementSyntax as x ->
                let condition = CSharpStatementWalker.ParseCsharpNode x.Condition
                let statement = CSharpStatementWalker.ParseCsharpNode x.Statement
                let elseExpr = x.Else |>  Option.ofObj |> Option.map CSharpStatementWalker.ParseCsharpNode
                Expr.IfThenElse (condition, statement, elseExpr, false)

            //| :? LabeledStatementSyntax as x -> "LabeledStatement" |> toLongIdent
            | :? LocalDeclarationStatementSyntax as x -> 
                x.Declaration |> CSharpStatementWalker.ParseCsharpNode
            //| :? LocalFunctionStatementSyntax as x -> "LocalFunctionStatement" |> toLongIdent
            //| :? LockStatementSyntax as x -> "LockStatement" |> toLongIdent
            | :? ReturnStatementSyntax as x -> 
                if x.Expression = null then 
                    Expr.Const SynConst.Unit
                else 
                    CSharpStatementWalker.ParseExpression x.Expression |> Expr.ReturnFromIf
            //| :? SwitchStatementSyntax as x -> "SwitchStatement" |> toLongIdent
            | :? ThrowStatementSyntax as x -> 
                let longIdent = toLongIdent "raise"
                ExprOps.toAtomicApp longIdent (CSharpStatementWalker.ParseExpression x.Expression |> Expr.Paren)

            | :? TryStatementSyntax as x -> 

                let catches = 
                    x.Catches 
                    |> Seq.toList
                    |> List.map (fun x -> 
                        let t = x.Declaration.Type |> parseType
                        let expr = CSharpStatementWalker.ParseCsharpNode x.Block
                        match x.Declaration.Identifier.WithoutTrivia().ToFullString() with 
                        | "" | null -> MatchClause.Clause (SynPat.IsInst (t,range0), None, expr)  
                        | name ->                     
                            MatchClause.Clause (SynPat.Named (SynPat.IsInst (t, range0), toSingleIdent name, false, None, range0), None, expr) )   

                Expr.TryWith (CSharpStatementWalker.ParseCsharpNode x.Block, catches)
                
            //| :? UnsafeStatementSyntax as x -> "UnsafeStatement" |> toLongIdent
            | :? UsingStatementSyntax as x -> 

                let (init, name) = x.Declaration.Variables |> Seq.head |> (fun x -> 
                    CSharpStatementWalker.ParseCsharpNode x.Initializer, x.Identifier.ValueText |> toSingleIdent)

                let var = 
                    FSharpBinding.LetBind 
                        (
                            None, SynBindingKind.NormalBinding, false, false, [], 
                            SynValData (None, SynValInfo ([], SynArgInfo ([], false, None )), None), 
                            Pat.Named (Pat.Wild, name, false, None), init)
                let s = x.Statement |> CSharpStatementWalker.ParseCsharpNode

                Expr.LetOrUse (false, true, [var], s)

            | :? WhileStatementSyntax as x -> 
                let body = CSharpStatementWalker.ParseStatementSyntax x.Statement |> replaceAnyPostOrPreIncrement
                let expr = CSharpStatementWalker.ParseExpressionWithVariables x.Condition

                match containsExpr (function | Expr.LongIdentSet _ -> true | _ -> false) expr with 
                | [Expr.LongIdentSet(x,y)] -> 

                    let sequence = 
                        let walker tree = 
                            match tree with 
                            | Expr.Sequential(b,Expr.LongIdentSet (c,d), cond) -> Some tree
                            | Expr.Sequential(b, cond, Expr.LongIdentSet (c,d)) -> Some tree
                            | _ -> None
                        getFirstExpr walker expr

                    let replaceSequence node = 
                        let walker tree = 
                            match tree with 
                            | Expr.Sequential(b,Expr.LongIdentSet (c,d), cond) -> Some cond
                            | Expr.Sequential(b, cond, Expr.LongIdentSet (c,d)) -> Some cond
                            | _ -> None
                        replaceExpr walker node
            
                    match sequence with
                    | Some (Expr.Sequential(b,Expr.LongIdentSet (c,d), _)) -> 
                        let body = Expr.Sequential(b,body, Expr.LongIdentSet (c,d))
                        let w = Expr.While (replaceSequence expr, body)
                        Expr.Sequential(b,Expr.LongIdentSet (c,d), w)

                    | Some (Expr.Sequential(b, cond, Expr.LongIdentSet (c,d))) -> 

                        let body = Expr.Sequential(b, Expr.LongIdentSet (c,d), body)
                        Expr.While (replaceSequence expr, body)
                    | _ -> 
                        let mutate = Expr.LongIdentSet(x,y)
                        let body = Expr.Sequential (false, body, mutate)

                        let expr = replaceExpr (function | Expr.LongIdentSet(a,b) -> Expr.LongIdent( false, a) |> Some | _ -> None) expr

                        let whileExpr = Expr.While (expr, body)
                        Expr.Sequential (false, mutate, whileExpr)

                | _ -> 
                    Expr.While (expr, body)
                    

            | :? YieldStatementSyntax as x -> 
                let e = CSharpStatementWalker.ParseExpression x.Expression
                Expr.YieldOrReturn ((true, false), e) 
            | e -> 
                let ident = createErrorCode "ParseStatementSyntax" node
                Expr.LongIdent (false, ident)
            
        match CSharpStatementWalker.GetLeadingTrivia node with
        | NoTrivia -> result
        | trivia -> Expr.Trivia(result, trivia)

    static member ParseCsharpNode (node:CSharpSyntaxNode):Expr =
        
        let trivia = CSharpStatementWalker.GetLeadingTrivia node
        
        let parsedNode = 
            match node with 

            | null -> Expr.Null
            | :? IdentifierNameSyntax as x -> 
                x.Identifier.WithoutTrivia().ToFullString() |> toLongIdent

            | :? AccessorDeclarationSyntax as x -> "AccessorDeclaration" |> toLongIdent
            | :? AccessorListSyntax as x -> "AccessorList" |> toLongIdent
            | :? AnonymousObjectMemberDeclaratorSyntax as x -> "AnonymousObjectMemberDeclarator" |> toLongIdent
            | :? ArgumentSyntax as x -> x.Expression |> CSharpStatementWalker.ParseExpression
            | :? ArrayRankSpecifierSyntax as x -> "ArrayRankSpecifier" |> toLongIdent
            | :? ArrowExpressionClauseSyntax as x -> x.Expression |> CSharpStatementWalker.ParseExpression
            | :? AttributeArgumentListSyntax as x -> 
                x.Arguments |> Seq.toList 
                |> List.map CSharpStatementWalker.ParseCsharpNode
                |> Expr.Tuple 
                |> Expr.Paren

            | :? AttributeArgumentSyntax as x -> CSharpStatementWalker.ParseExpression (x.Expression)
            | :? AttributeListSyntax as x -> "AttributeList" |> toLongIdent
            | :? AttributeSyntax as x -> "Attribute" |> toLongIdent
            | :? AttributeTargetSpecifierSyntax as x -> "AttributeTargetSpecifier" |> toLongIdent
            | :? BaseArgumentListSyntax as x -> 
                x.Arguments |> Seq.map (fun x -> CSharpStatementWalker.ParseCsharpNode x.Expression) 
                |> Seq.toList |> Expr.Tuple |> Expr.Paren

                //"BaseArgumentList" |> toLongIdent
            | :? BaseCrefParameterListSyntax as x -> "BaseCrefParameterList" |> toLongIdent
            | :? BaseListSyntax as x -> "BaseList" |> toLongIdent
            | :? BaseParameterListSyntax as x -> "BaseParameterList" |> toLongIdent
            | :? BaseTypeSyntax as x -> 

                printfn "| :? BaseTypeSyntax"
                printfn "%A" x
                printfn "%A" <| x.Kind()

                "BaseType" |> toLongIdent
            | :? CatchClauseSyntax as x -> "CatchClause" |> toLongIdent
            | :? CatchDeclarationSyntax as x -> "CatchDeclaration" |> toLongIdent
            | :? CatchFilterClauseSyntax as x -> "CatchFilterClause" |> toLongIdent
            | :? CompilationUnitSyntax as x -> "CompilationUnit" |> toLongIdent
            | :? ConstructorInitializerSyntax as x -> "ConstructorInitializer" |> toLongIdent
            | :? CrefParameterSyntax as x -> "CrefParameter" |> toLongIdent
            | :? CrefSyntax as x -> "Cref" |> toLongIdent
            | :? ElseClauseSyntax as x -> x.Statement |> CSharpStatementWalker.ParseStatementSyntax
            | :? EqualsValueClauseSyntax as x -> x.Value |> CSharpStatementWalker.ParseExpression
            | :? ExplicitInterfaceSpecifierSyntax as x -> "ExplicitInterfaceSpecifier" |> toLongIdent
            | :? ExpressionSyntax as x -> CSharpStatementWalker.ParseExpression (x)
            | :? ExternAliasDirectiveSyntax as x -> "ExternAliasDirective" |> toLongIdent
            | :? FinallyClauseSyntax as x -> "FinallyClause" |> toLongIdent
            | :? InterpolatedStringContentSyntax as x -> "InterpolatedStringContent" |> toLongIdent
            | :? InterpolationAlignmentClauseSyntax as x -> "InterpolationAlignmentClause" |> toLongIdent
            | :? InterpolationFormatClauseSyntax as x -> "InterpolationFormatClause" |> toLongIdent
            | :? JoinIntoClauseSyntax as x -> "JoinIntoClause" |> toLongIdent
            | :? MemberDeclarationSyntax as x -> "MemberDeclaration" |> toLongIdent
            | :? NameColonSyntax as x -> "NameColon" |> toLongIdent
            | :? NameEqualsSyntax as x -> "NameEquals" |> toLongIdent
            | :? OrderingSyntax as x -> "Ordering" |> toLongIdent
            | :? ParameterSyntax as x -> "Parameter" |> toLongIdent
            | :? PatternSyntax as x -> "Pattern" |> toLongIdent
            | :? QueryBodySyntax as x -> "QueryBody" |> toLongIdent
            | :? QueryClauseSyntax as x -> "QueryClause" |> toLongIdent
            | :? QueryContinuationSyntax as x -> "QueryContinuation" |> toLongIdent
            | :? SelectOrGroupClauseSyntax as x -> "SelectOrGroupClause" |> toLongIdent
            | :? StatementSyntax as x -> x |> CSharpStatementWalker.ParseStatementSyntax
            | :? StructuredTriviaSyntax as x -> "StructuredTrivia" |> toLongIdent
            | :? SwitchLabelSyntax as x -> "SwitchLabel" |> toLongIdent
            | :? SwitchSectionSyntax as x -> "SwitchSection" |> toLongIdent
            | :? TupleElementSyntax as x -> "TupleElement" |> toLongIdent
            | :? TypeArgumentListSyntax as x -> "TypeArgumentList" |> toLongIdent
            | :? TypeParameterConstraintClauseSyntax as x -> "TypeParameterConstraintClause" |> toLongIdent
            | :? TypeParameterConstraintSyntax as x -> "TypeParameterConstraint" |> toLongIdent
            | :? TypeParameterListSyntax as x -> "TypeParameterList" |> toLongIdent
            | :? TypeParameterSyntax as x -> "TypeParameter" |> toLongIdent
            | :? UsingDirectiveSyntax as x ->  
                //x.Name |> 
                //x.Name
                 "UsingDirective" |> toLongIdent
            | :? VariableDeclarationSyntax as x -> 

                x.Variables 
                |> Seq.map (fun x -> x.Identifier.ValueText, CSharpStatementWalker.ParseCsharpNode x.Initializer) 
                |> Seq.map (fun (identifier, init) -> 
                    match init with 
                    | Expr.Null -> identifier, Expr.TypeApp (toLongIdent "Unchecked.defaultof", [ParserUtil.parseType x.Type])
                    | e -> identifier, e)
                |> Seq.map (fun (identifier, init) -> 

                    match init with 
                    | Expr.DoBang e -> 
                        Expr.LetOrUseBang (false, false, 
                            SynPat.LongIdent (toLongIdentWithDots identifier, None, None, SynArgPats.NamePatPairs ([], range0), None, range0), e, Expr.InLetPlaceholder)
                    | _ -> 
                        Expr.LetOrUse(
                            false, false, 
                            [LetBind(None, SynBindingKind.NormalBinding, false, true, [],
                                SynValData (
                                    None, SynValInfo ([], SynArgInfo ([], false, None )), None), 
                                Pat.Named (Pat.Wild, Ident(identifier, range0), false, None), init)], Expr.InLetPlaceholder) )
                |> Seq.toList
                |> function 
                | [] -> 
                    let ident = createErrorCode "VariableDeclarationSyntax" node
                    Expr.LongIdent (false, ident)
                | [x] -> x
                | xs -> 
                    xs  
                    |> List.reduce (fun x y -> 
                        match x with 
                        | Expr.LetOrUse(a,b,c,_) -> Expr.LetOrUse(a,b,c,y)
                        | e -> e )

            //| :? VariableDesignationSyntax as x -> x.with//"VariableDesignation" |> toLongIdent
            | :? WhenClauseSyntax as x -> "WhenClause" |> toLongIdent
            | :? XmlAttributeSyntax as x -> "XmlAttribute" |> toLongIdent
            | :? XmlElementEndTagSyntax as x -> "XmlElementEndTag" |> toLongIdent
            | :? XmlElementStartTagSyntax as x -> "XmlElementStartTag" |> toLongIdent
            | :? XmlNameSyntax as x -> "XmlName" |> toLongIdent
            | :? XmlNodeSyntax as x -> "XmlNode" |> toLongIdent
            | :? XmlPrefixSyntax as x -> "XmlPrefix" |> toLongIdent
            | e -> 
                let ident = createErrorCode "ParseChsarpNode" node
                Expr.LongIdent (false, ident)
                
        match trivia with
        | NoTrivia -> parsedNode
        | trivia -> Expr.Trivia(parsedNode, trivia)

    static member ParseExpression (node:ExpressionSyntax) =
        node
        |> CSharpStatementWalker.ParseExpressionWithVariables
        |> replaceAnyPostOrPreIncrement
        |> fun e ->
            match CSharpStatementWalker.GetLeadingTrivia node with
            | NoTrivia -> e
            | trivia -> Expr.Trivia(e, trivia)

    static member ParseExpressionWithVariables (node:ExpressionSyntax) = 

        if isNull node then  printfn "Node was null"; Expr.Const SynConst.Unit else

        let parsePrefixNode operand operatorToken = 
            let operator  = 
                match CSharpStatementWalker.ParseToken operatorToken with 
                | Expr.Ident "op_Subtraction" -> Expr.Ident "op_UnaryNegation" // The operators are the same, context dictates the change
                | e -> e
        
            operand
            |> CSharpStatementWalker.ParseExpression 
            |> function
            | FindIdent xs -> xs |> List.tryHead |> Option.map fst
            | _ -> None
            |> Option.map (fun operand -> 
                let operatorToken = 
                    match operatorToken with
                    | IsIncrement _ -> PrettyNaming.CompileOpName "op_Addition" |> Some
                    | IsDecrement _ -> PrettyNaming.CompileOpName "op_Subtraction" |> Some
                    | Neither -> None

                match operatorToken with
                | Some op ->
                    let app =  ExprOps.toInfixApp (toLongIdent operand) (Expr.Ident op) (1 |> SynConst.Int32 |> Expr.Const)
                    let assign = Expr.LongIdentSet (toLongIdentWithDots operand, app)
                    Expr.Sequential(false, assign, toLongIdent operand)
                | None -> ExprOps.toApp operator (toLongIdent operand)
            ) |> function 
            | Some x -> x
            | None -> ExprOps.toApp  operator (CSharpStatementWalker.ParseExpression operand)

        let result = 
            match node with
            | :? IsPatternExpressionSyntax as x -> 
                let expr = CSharpStatementWalker.ParseExpression x.Expression

                let clause = 
                    match x.Pattern with 
                    | :? DeclarationPatternSyntax as x -> 

                        let t = x.Type |> ParserUtil.parseType
                        match t with
                        | SynType.LongIdent y when joinLongIdentWithDots y = "var" -> 
                            let name = x.Designation.WithoutTrivia().ToString() |> toLongIdentWithDots
                            SynPat.LongIdent (name, None, None, SynArgPats.Pats [], None, range0)
                        | _ -> 
                            let name = x.Designation.WithoutTrivia().ToString() |> toSingleIdent
                            SynPat.Named (SynPat.IsInst (t,range0), name, false, None, range0)
                    | :? ConstantPatternSyntax as x -> 

                        let e = CSharpStatementWalker.ParseExpression x.Expression
                        match e with 
                        | Expr.Const x -> SynPat.Const (x, range0)
                        | Expr.Null ->  SynPat.Null range0
                        | _ -> 
                            let ident = createErrorCode "ConstantPatternSyntax" node 
                            SynPat.LongIdent (ident, None, None, SynArgPats.Pats [], None, range0)
                    | e -> 
                        let ident = createErrorCode "IsPatternExpressionSyntax" x 
                        SynPat.LongIdent (ident, None, None, SynArgPats.Pats [], None, range0)

                Expr.CsharpIsMatch (expr, clause )

            | :? IdentifierNameSyntax as x -> CSharpStatementWalker.ParseCsharpNode x
            | :? AnonymousFunctionExpressionSyntax as x -> 
                match x with 
                | :? LambdaExpressionSyntax as x -> 
                    match x with 
                    | :? SimpleLambdaExpressionSyntax as x -> 
                        let b = x.Body |> CSharpStatementWalker.ParseCsharpNode
                        let n = 
                            SynSimplePats.SimplePats 
                                ([
                                    SynSimplePat.Id 
                                        (Ident(x.Parameter.Identifier.ValueText, range0), 
                                            None, false, true, false, range0)] , range0)

                        Expr.Lambda (true, false, n, b, None)
                    | :? ParenthesizedLambdaExpressionSyntax as x -> 

                        let args = 
                            let idents = 
                                if x.ParameterList = null then []
                                else 
                                    x.ParameterList.Parameters 
                                    |> Seq.map (fun x -> toPatId x.Identifier.ValueText)
                                    |> Seq.toList
                            SynSimplePats.SimplePats (idents, range0)

                        let body = x.Body |> CSharpStatementWalker.ParseCsharpNode
                        Expr.Lambda (true, false, args, body, None)
                    | _ -> Expr.LongIdent(false, createErrorCode "LambdaExpressionSyntax" x)

                | :? AnonymousMethodExpressionSyntax as x -> 

                    let args = 
                        let idents = 
                            if x.ParameterList = null then []
                            else 
                                x.ParameterList.Parameters 
                                |> Seq.map (fun x -> toPatId x.Identifier.ValueText)
                                |> Seq.toList
                        SynSimplePats.SimplePats (idents, range0)

                    let body = x.Body |> CSharpStatementWalker.ParseCsharpNode
                    Expr.Lambda (true, false, args, body, None)
                | _ -> Expr.LongIdent(false, createErrorCode "AnonymousFunctionExpressionSyntax" x)
                    

            //| :? AnonymousObjectCreationExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "AnonymousObjectCreationExpressionSyntax"
            | :? ArrayCreationExpressionSyntax as x -> 
                let size = x.Type.RankSpecifiers |> Seq.head |> (fun x -> x.Sizes) |> Seq.toList |> List.map (CSharpStatementWalker.ParseExpression) |> List.head
                let t = x.Type.ElementType.WithoutTrivia().ToString() |> fixKeywords  |> toLongIdentWithDots |> SynType.LongIdent
                let init = CSharpStatementWalker.ParseExpression x.Initializer

                let typeApp = Expr.TypeApp (toLongIdent "Array.zeroCreate", [t] )
                ExprOps.toApp typeApp (Expr.Paren size)
                
            | :? AssignmentExpressionSyntax as x -> x |> CSharpStatementWalker.ParseAssignmentExpressionSyntax
            | :? AwaitExpressionSyntax as x -> 
                let expr = CSharpStatementWalker.ParseExpression x.Expression
                Expr.DoBang expr
            
            | :? BinaryExpressionSyntax as x -> x |> CSharpStatementWalker.ParseBinaryExpression
            | :? CastExpressionSyntax as x -> 
                let exp = CSharpStatementWalker.ParseExpression x.Expression
                let castType = ParserUtil.parseType x.Type
                Expr.Downcast (exp, castType) |> Expr.Paren

            //| :? CheckedExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "CheckedExpressionSyntax"
            //| :? ConditionalAccessExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ConditionalAccessExpressionSyntax"
            | :? ConditionalExpressionSyntax as x -> 
                let cond = CSharpStatementWalker.ParseExpression x.Condition
                let thenCond = CSharpStatementWalker.ParseExpression x.WhenTrue
                let elseCond = CSharpStatementWalker.ParseExpression x.WhenFalse

                Expr.IfThenElse (cond, thenCond, Some elseCond, false)

            //| :? DeclarationExpressionSyntax as x -> x.Designation x.Type |> ParserUtil.parseType
            //| :? DefaultExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "DefaultExpressionSyntax"
            | :? ElementAccessExpressionSyntax as x -> 
                let e = CSharpStatementWalker.ParseExpression x.Expression

                x.ArgumentList.Arguments 
                |> Seq.map (fun x -> CSharpStatementWalker.ParseCsharpNode x )
                |> Seq.toList
                |> function 
                | [x] -> Expr.DotIndexedGet(e, [IndexerArg.One (x)])
                | _ -> 
                    let ident = createErrorCode "ElementAccessExpressionSyntax" node
                    Expr.LongIdent (false, ident)


            //| :? ElementBindingExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ElementBindingExpressionSyntax"
            | :? ImplicitArrayCreationExpressionSyntax as x -> 
                CSharpStatementWalker.ParseExpression (x.Initializer)
                
            //| :? ImplicitElementAccessSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ImplicitElementAccessSyntax"
            | :? ImplicitStackAllocArrayCreationExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ImplicitStackAllocArrayCreationExpressionSyntax"
            | :? InitializerExpressionSyntax as x -> 

                x.Expressions
                |> Seq.map (CSharpStatementWalker.ParseExpression >> (fun x -> 
                    match x with 
                    | Expr.LongIdentSet (LongIdentWithDots ([ident], _), value) -> 
                        ExprOps.toInfixApp (Expr.Ident ident.idText) (Expr.Ident "op_Equality") value
                    | _ -> x))
                |> Seq.toList
                |> sequential
                |> (fun x -> Expr.ArrayOrListOfSeqExpr (true, x))

            | :? BaseExpressionSyntax -> toLongIdent "base"
            | :? InstanceExpressionSyntax -> toLongIdent "this"
            | :? InterpolatedStringExpressionSyntax as x -> 

                let args = 
                    x.Contents 
                    |> Seq.map CSharpStatementWalker.ParseInterpolatedStringContentSyntax
                    |> Seq.toList

                let stringFormat = 
                    args 
                    |> List.map (function Expr.Const (SynConst.String (c,_)) -> c | _ -> "%O")
                    |> String.concat ""

                let args = args |> List.choose (function Expr.Paren _ as e -> Some e | _ -> None)

                (toLongIdent "sprintf" :: (Expr.Const (SynConst.String (stringFormat, range0))) :: args)
                |> List.reduce ExprOps.toApp
            | :? InvocationExpressionSyntax as x -> 
            
                let args =  
                    x.ArgumentList.Arguments 
                    |> Seq.map CSharpStatementWalker.ParseCsharpNode 
                    |> Seq.toList
                    |> Expr.Tuple 
                    |> Expr.Paren

                let expr = x.Expression |> CSharpStatementWalker.ParseExpression
                ExprOps.toApp expr args

            //| :? IsPatternExpressionSyntax as x -> ()
            | :? LiteralExpressionSyntax as x -> 
                x.Token |> CSharpStatementWalker.ParseToken
            //| :? MakeRefExpressionSyntax as x -> ()
            | :? MemberAccessExpressionSyntax as x ->

                match x.OperatorToken.Text with 
                | "." -> 
                    let invocation = x.Expression |> CSharpStatementWalker.ParseExpression |> ExprOps.withParenIfReq               
                    let (longIdent, types) = x.Name.WithoutTrivia() |> parseType |> synTypeToExpr 
                    let dotGet = Expr.DotGet (invocation, longIdent)

                    match types with
                    | [] -> dotGet
                    | types -> Expr.TypeApp (dotGet, types)
                | _ -> 
                    Expr.LongIdent(false, createErrorCode "MemberAccessExpressionSyntax" x)

            //| :? MemberBindingExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "MemberBindingExpressionSyntax"
            | :? ObjectCreationExpressionSyntax as x -> 

                let typeName = x.Type.WithoutTrivia() |> ParserUtil.parseType

                let init = 
                    match x.Initializer with
                    | null ->  Expr.Const SynConst.Unit
                    | initExp ->  CSharpStatementWalker.ParseExpression (initExp)

                let joinArgs xs = 
                    match xs, init with 
                    | Expr.Tuple xs, Expr.Tuple ys -> Expr.Tuple (xs @ ys)
                    | Expr.Tuple xs, Expr.Const SynConst.Unit -> Expr.Tuple xs
                    | Expr.Tuple [], Expr.ArrayOrListOfSeqExpr (_, expr) as x ->  

                        let isClassMemberInitialisation = 
                            expr 
                            |> containsExpr (function 
                                | Expr.App (_, _, Expr.Ident "op_Equality", _) -> true
                                | _ -> false )
                            |> List.isEmpty
                            |> not

                        if isClassMemberInitialisation then 
                            expr |> ExprOps.sequentialToList |> Expr.Tuple
                        else snd x // Handles init for list ie new List<int>([| 1; 2; |])
                    | _, _ -> failwithf "Unexpected syntax constructing class: %s" <| x.Type.ToFullString()                

                let args = 
                    let args = 
                        match x.ArgumentList with 
                        | null -> Expr.Const SynConst.Unit
                        | x -> CSharpStatementWalker.ParseCsharpNode x

                    match args with
                    | Expr.Paren xs -> joinArgs xs
                    | Expr.Const SynConst.Unit -> joinArgs (Expr.Tuple [])
                    | _ -> failwithf "Unexpected syntax constructing class: %s" <| x.Type.ToFullString()

                Expr.New (false, typeName, Expr.Paren args)
            //| :? OmittedArraySizeExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "OmittedArraySizeExpressionSyntax"
            | :? ParenthesizedExpressionSyntax as x -> CSharpStatementWalker.ParseExpression x.Expression
            | :? PostfixUnaryExpressionSyntax as x -> 
                
                match parsePrefixNode x.Operand x.OperatorToken with 
                // TODO: review this, it doesn't appear to be doing anything
                | Expr.Sequential (b, assign, postfixOp) -> Expr.Sequential (b, postfixOp, assign)
                | e -> e
                
            | :? PrefixUnaryExpressionSyntax as x -> 
                match x.Kind() with 
                | SyntaxKind.LogicalNotExpression -> 
                    ExprOps.toApp (PrettyNaming.CompileOpName "not" |> Expr.Ident) (x.Operand |> CSharpStatementWalker.ParseExpression)
                | _ -> 
                    parsePrefixNode x.Operand x.OperatorToken

                    
            //| :? QueryExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "QueryExpressionSyntax"
            //| :? RefExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "RefExpressionSyntax"
            //| :? RefTypeExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "RefTypeExpressionSyntax"
            //| :? RefValueExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "RefValueExpressionSyntax"
            //| :? SizeOfExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "SizeOfExpressionSyntax"
            //| :? StackAllocArrayCreationExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "StackAllocArrayCreationExpressionSyntax"
            //| :? ThrowExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ThrowExpressionSyntax"
            //| :? TupleExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "TupleExpressionSyntax"
            | :? TypeOfExpressionSyntax as x -> 
                let ident = x.Type |> parseType |> List.singleton
                Expr.TypeApp (Expr.Ident "typeof", ident)

            | :? TypeSyntax as x -> 
                x.WithoutTrivia().ToString() |> fixKeywords |> toLongIdent

                // x.WithoutTrivia().ToFullString() |> toLongIdent
            | _ ->  Expr.LongIdent(false, createErrorCode "ParseExpressionWithVariables" node)
        
        match CSharpStatementWalker.GetLeadingTrivia node with
        | NoTrivia -> result
        | trivia -> Expr.Trivia(result, trivia)

    static member ParseNodeOrToken(node:SyntaxNodeOrToken): Expr = 

        if node.IsToken then 
            node.AsToken() |> CSharpStatementWalker.ParseToken
        else 
            node.AsNode() 
            |> CSharpStatementWalker.ToCsharpSyntaxNode
            |> Option.map (fun x -> CSharpStatementWalker.ParseCsharpNode x)
            |> function 
                | Some x -> x
                | None -> failwith "VB is not supported"
                
    static member GetLeadingTrivia(node: SyntaxNode): Trivia<string> =
        
        if node = null || node.GetLeadingTrivia() |> Seq.isEmpty then
            NoTrivia
        else    
            node.GetLeadingTrivia()
            |> Seq.filter (fun x -> x.ToString() |> String.IsNullOrWhiteSpace |> not)
            |> Seq.map (fun x -> x.ToFullString().Trim())
            |> String.concat "\n"
            |> fun x ->
                if String.IsNullOrEmpty x then NoTrivia
                else Above x
        


type FSharperTreeBuilder() = 
    inherit CSharpSyntaxWalker(SyntaxWalkerDepth.Token)
    
    member this.VisitNamespaceDeclaration (node:NamespaceDeclarationSyntax ) = 

        let classes = 
            node.ChildNodes().OfType<ClassDeclarationSyntax>()
            |> Seq.collect this.VisitClassDeclaration
            |> Seq.toList

        let interfaces = 
            node.ChildNodes().OfType<InterfaceDeclarationSyntax>()
            |> Seq.map this.VisitInterfaceDeclaration
            |> Seq.toList
            |> List.map Structure.Interface

        let enums =
            node.ChildNodes().OfType<EnumDeclarationSyntax>()
            |> Seq.map this.VisitEnumDeclaration
            |> Seq.toList
            |> List.map Structure.E        
            
        {
            Namespace.Name = node.Name.WithoutTrivia().ToFullString()
            Namespace.Structures = interfaces @ classes @ enums
        } 

    member this.VisitEnumDeclaration(node:EnumDeclarationSyntax) =
        let attrs = 
            node.AttributeLists
            |> Seq.collect (fun x -> 
                x.Attributes 
                |> Seq.map this.ParseAttributeSyntax)
            |> Seq.toList
        
        let enumMembers =
            node.Members
            |> Seq.collect this.VisitEnumMemberDeclaration
            |> Seq.toList        

        {
            Enum.Name = node.Identifier.ValueText
            Members = enumMembers
            Attributes = attrs
        } 
        
    member this.VisitEnumMemberDeclaration(node:EnumMemberDeclarationSyntax) =
        
        node.EqualsValue  // TODO: this needs more work. When null it is producing an invalid F# syntax tree
        |> Option.ofObj
        |> Option.map (fun x -> 
            let nodeValueExpr = CSharpStatementWalker.ParseCsharpNode x.Value
            EnumMemberValue (node.Identifier.ValueText, nodeValueExpr) )
        |> Option.toList


    member this.VisitInterfaceDeclaration(node:InterfaceDeclarationSyntax) =    

        let members = 
            node.Members 
            |> Seq.map (fun x -> 
                match x with
                | :? BaseMethodDeclarationSyntax as x -> 
                    match x with 
                    | :? MethodDeclarationSyntax as x -> 
                        let returnType = x.ReturnType |> parseType
                        let parameters = 
                            x.ParameterList.Parameters |> Seq.map (fun x -> 
                                x.Type |> parseType) |> Seq.toList
                        let ident = x.Identifier.WithLeadingTrivia().Text |> toSingleIdent

                        let parameters = 
                            match parameters with
                            | [] -> SynType.LongIdent (toLongIdentWithDots "unit") |> List.singleton
                            | _ -> parameters

                        let parameters = parameters @ [returnType]                            
                        InterfaceMethod (ident, parameters)                                                  

                // | :? BasePropertyDeclarationSyntax as x -> x.ToFullString()
                // | :? BaseTypeDeclarationSyntax as x -> x.ToFullString()
                // | :? DelegateDeclarationSyntax as x -> x.ToFullString()
                // | :? EnumMemberDeclarationSyntax as x -> x.ToFullString()
                // | :? GlobalStatementSyntax as x -> x.ToFullString()
                // | :? IncompleteMemberSyntax as x -> x.ToFullString()
                // | :? NamespaceDeclarationSyntax as x -> x.ToFullString()
                ) |> Seq.toList
        (node.Identifier.WithoutTrivia().Text |> toSingleIdent, members)

    member this.ParseAttributeSyntax(node: AttributeSyntax) = 
        let attributesValues = 
            node.ArgumentList
            |> Option.ofObj
            |> Option.map (fun x -> x.Arguments)
            |> (Option.toList >> List.toSeq >> Seq.concat)
            |> Seq.map (fun y -> 

                if isNull y.NameEquals then 
                    CSharpStatementWalker.ParseCsharpNode y.Expression |>  AttributeValue
                else 
                    NamedAttributeValue 
                        (CSharpStatementWalker.ParseCsharpNode y.NameEquals.Name, 
                        CSharpStatementWalker.ParseCsharpNode y.Expression) )
            |> Seq.toList

        {
            Attribute.Name = node.Name.WithoutTrivia().ToFullString() |> toLongIdentWithDots
            Attribute.Parameters = attributesValues
            Target = None;
            AppliesToGetterAndSetter = false
        }

    member this.VisitAttributeListSyntax(node:AttributeListSyntax) = 
        let setAssembly x = {x with Target = node.Target.ToFullString().Replace(":", "").Trim() |> toSingleIdent |> Some }
        node.Attributes |> Seq.map (this.ParseAttributeSyntax >> setAssembly) |> Seq.toList

    member this.VisitClassDeclaration(node:ClassDeclarationSyntax ): Structure list =

        let attrs = 
            node.AttributeLists
            |> Seq.collect (fun x -> 
                x.Attributes 
                |> Seq.map this.ParseAttributeSyntax)
            |> Seq.toList

        let typeParameters = 
            node.TypeParameterList 
            |> Option.ofObj 
            |> Option.map (fun x -> 
                x.Parameters 
                |> Seq.map (fun x -> x.Identifier.WithoutTrivia().Text) 
                |> Seq.toList )
            |> Option.toList
            |> List.concat

        let methods = 
            node.ChildNodes().OfType<MethodDeclarationSyntax>()
            |> Seq.map this.VisitMethodDeclaration
            |> Seq.toList

        let rec doesBaseTypeBeginWithI = function
        | SynType.App  (x, _, _, _, _, _, _) -> doesBaseTypeBeginWithI x
        | SynType.LongIdent x -> 
            // TODO: this won't work for System.IDisposable ie prefix with namespace
            match joinLongIdentWithDots x with 
            | x when x.StartsWith "I" -> true
            | _ -> false
        | _ -> false

        let containsPublicNonOverrideMethod = 
            methods |> List.filter (fun x -> not x.IsOverride && not x.IsPrivate) |> List.length

        let containsOverrideMethod = 
            methods |> List.filter (fun x -> x.IsOverride) |> List.length

        let baseTypes = 
            node.BaseList 
            |> Option.ofObj 
            |> Option.bind (fun x -> 
                x.Types 
                |> Seq.map (fun x -> ParserUtil.parseType x.Type)
                |> Seq.toList
                |> function 
                | [] -> None
                | x::_ as xs when doesBaseTypeBeginWithI x && containsOverrideMethod = 0 -> Some (None, xs) 
                | x::xs -> Some (Some x, xs)  )

        let ctors = 
            node.ChildNodes().OfType<ConstructorDeclarationSyntax>()
            |> Seq.map this.VisitConstructorDeclaration
            |> Seq.toList

        let fields = 
            node.ChildNodes().OfType<FieldDeclarationSyntax>()
            |> Seq.collect this.VisitFieldDeclaration
            |> Seq.toList

        let publicFields = 
            fields 
            |> List.filter (fun x -> x.IsPublic)
            |> List.map (fun x -> 
                {
                    Prop.Name = SynPat.getName x.Name
                    Type = x.Type
                    Prop.Get = x.Initializer
                    Prop.Set = None
                    Access = None
                } 
            )

        let properties = 
            node.ChildNodes().OfType<PropertyDeclarationSyntax>()
            |> Seq.map this.VisitPropertyDeclaration
            |> Seq.toList

        let innerClasses = 
            node.ChildNodes().OfType<ClassDeclarationSyntax>()
            |> Seq.collect this.VisitClassDeclaration
            |> Seq.toList

        let innerEnums =
            node.ChildNodes().OfType<EnumDeclarationSyntax>()
            |> Seq.map this.VisitEnumDeclaration
            |> Seq.toList
            |> List.map E // Convert to structure type (being that of enum)
            
        let trivia = CSharpStatementWalker.GetLeadingTrivia node

        let klass = 
            {
                Name = { ClassName.Name = node.Identifier.ValueText; Generics = [] }
                ImplementInterfaces = baseTypes |> Option.map (snd) |> Option.toList |> List.concat
                BaseClass = baseTypes |> Option.bind fst
                Constructors = ctors
                Fields = (fields |> List.filter (fun x -> not x.IsPublic)) // public fields are not a thing in F#
                Methods = methods
                Properties = properties @ publicFields
                TypeParameters = typeParameters
                Attributes = attrs
                Trivia = trivia
            } |> C // Convert to structure type (being that of a class)

        innerEnums @ innerClasses @ [klass]


    member this.VisitConstructorDeclaration (node:ConstructorDeclarationSyntax) = 
        {
            Ctor.Body = 
                node.Body.Statements
                |> Seq.toList
                |> List.map (fun x -> CSharpStatementWalker.ParseCsharpNode(x))

            Ctor.Parameters = 
                node.ParameterList.Parameters 
                |> Seq.map (fun x -> 

                    let name = x.Identifier.WithoutTrivia().Text
                    let typee = ParserUtil.parseType x.Type
                    let typeArg = 
                        SynSimplePat.Typed
                            (SynSimplePat.Id 
                                (toSingleIdent name, None, 
                                false, false, false, range0), typee, range0) 

                    if x.Modifiers |> Seq.exists (fun x -> x.ToString() = "params") then 
                        let attribs = 
                            {
                                Attributes = [{
                                        SynAttribute.TypeName = toLongIdentWithDots "ParamArray"
                                        SynAttribute.ArgExpr = SynExpr.Const (SynConst.Unit, range0)
                                        SynAttribute.Target = None;
                                        SynAttribute.AppliesToGetterAndSetter = false;
                                        SynAttribute.Range = range0}]
                                Range = range0 
                            }

                        SynSimplePat.Attrib (typeArg, [attribs], range0)
                                
                    else typeArg )
                |> Seq.toList

            SubclassArgs = 
                node.Initializer |> Option.ofObj 
                |> Option.bind (fun x -> Option.ofObj x.ArgumentList)
                |> Option.map (fun x -> x.Arguments)
                |> Option.map (fun x -> 
                    x |> Seq.map (fun x -> x.WithoutTrivia().ToFullString()) |> Seq.toList)
                |> Option.toList
                |> List.concat
        }

    member this.VisitMethodDeclaration(node:MethodDeclarationSyntax ) = 
        let isPrivate = 
            node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.PrivateKeyword)

        let isStatic = 
            node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.StaticKeyword)

        let body = 
            match Option.ofObj node.Body, Option.ofObj node.ExpressionBody with 
            | Some body, Some eBody -> Some (body :> CSharpSyntaxNode) // WTF: why is this event possible in the domain. It makes no sense
            | Some body, _ -> Some (body :> CSharpSyntaxNode)
            | None, Some expressionBody -> Some (expressionBody :> CSharpSyntaxNode)
            | _, _ -> None
                
        {
            Method.Name = node.Identifier.WithoutTrivia().Text
            Method.IsVirtual = node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.VirtualKeyword )
            Method.IsAsync = node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.AsyncKeyword )
            Method.IsPrivate = isPrivate
            Method.IsOverride = node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.OverrideKeyword )
            Method.IsStatic = isStatic
            Method.Trivia = CSharpStatementWalker.GetLeadingTrivia node
            Method.ReturnType = node.ReturnType |> ParserUtil.parseType
            Method.Parameters = 
                node.ParameterList.Parameters 
                |> Seq.map (fun x -> 

                    let name = x.Identifier.WithoutTrivia().Text
                    let typee = ParserUtil.parseType x.Type
                    let typeArg = SynPat.Typed (SynPat.Named (SynPat.Wild range0, Ident(name, range0), false, None, range0), typee, range0)

                    if x.Modifiers |> Seq.exists (fun x -> x.ToString() = "params") then 
                        let attribs = 
                            {
                                Attributes = [{
                                        SynAttribute.TypeName = toLongIdentWithDots "ParamArray"
                                        SynAttribute.ArgExpr = SynExpr.Const (SynConst.Unit, range0)
                                        SynAttribute.Target = None;
                                        SynAttribute.AppliesToGetterAndSetter = false;
                                        SynAttribute.Range = range0}]
                                Range = range0
                            }
                            
                        let typeArg = 
                            SynPat.Attrib (typeArg, [attribs], range0)
                        SynPat.Paren(typeArg, range0)
                    else typeArg )
                |> Seq.toList
            Method.Body =
                body |> Option.map (CSharpStatementWalker.ParseCsharpNode )
                |> function 
                | Some x -> x
                | None -> Expr.Const SynConst.Unit

            Method.Accessibility = if isPrivate then Some SynAccess.Private else None
            Method.Attributes = 
                node.AttributeLists 
                |> Seq.map (fun x -> x.Attributes |> Seq.map (fun x -> 
                    let name = x.Name.WithoutTrivia().ToFullString() |> toLongIdentWithDots
                    let args = 
                        match x.ArgumentList with 
                        | null -> None
                        | x -> CSharpStatementWalker.ParseCsharpNode x |> Some 

                    name, args ))
                |> Seq.concat
                |> Seq.toList
        }

    member this.VisitFieldDeclaration (node:FieldDeclarationSyntax): Field seq = 

        let isPublic = node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.PublicKeyword)
        let isConstant = node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.ConstKeyword)
        let isStatic = node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.StaticKeyword)

        node.Declaration.Variables
        |> Seq.map (fun x -> 
            let name = 
                SynPat.LongIdent
                    (LongIdentWithDots (x.Identifier.WithoutTrivia().ToFullString() |> toIdent, [range0]), None, None, 
                        Pats ([]), None, range0 )
            {
                Field.Name =  name
                Field.Type = node.Declaration.Type.WithoutTrivia() |> parseType
                Field.IsPublic = isPublic
                Field.Initializer = x.Initializer |> Option.ofObj |> Option.map (fun x -> x.Value |> CSharpStatementWalker.ParseExpression )
                Field.IsConst = isConstant
                Field.IsStatic = isStatic
            })

    member this.VisitPropertyDeclaration (node:PropertyDeclarationSyntax) = 

        let parseAccessorDeclaration (node:AccessorDeclarationSyntax) = 

            let expression = node.ExpressionBody |> Option.ofObj
            let statement = node.Body |> Option.ofObj 

            match expression, statement with 
            | None, None -> []
            | Some e, None -> [CSharpStatementWalker.ParseCsharpNode e]
            | None, Some s -> s.Statements |> Seq.map CSharpStatementWalker.ParseCsharpNode |> Seq.toList
            | Some e, Some s -> [CSharpStatementWalker.ParseCsharpNode e;] @ (s.Statements |> Seq.map CSharpStatementWalker.ParseCsharpNode |> Seq.toList)

        let processAccessorForAccessorType accessor = 
            match node.AccessorList, accessor with
            | null, SyntaxKind.GetAccessorDeclaration -> node.ExpressionBody |> CSharpStatementWalker.ParseCsharpNode |> Some
            | null, SyntaxKind.SetAccessorDeclaration -> None
            | _, _ -> 
                node.AccessorList.Accessors 

                |> Seq.filter (fun x -> x.Kind() = accessor)
                |> Seq.map parseAccessorDeclaration
                |> Seq.toList
                |> List.concat
                |> function
                | _::_::_ as xs -> xs |> sequential |> Some
                | [ x ] -> x |> Some
                | _ -> None

        let getStatements = processAccessorForAccessorType SyntaxKind.GetAccessorDeclaration
        let setStatements =  processAccessorForAccessorType SyntaxKind.SetAccessorDeclaration

        let access = 
            match node.Modifiers |> Seq.map (fun x -> x.WithoutTrivia().ToFullString()) |> Seq.toList with 
            | ["internal"] -> Some SynAccess.Internal
            | ["private"] -> Some SynAccess.Private
            | _  -> None
            
        {
            Prop.Name = node.Identifier.WithoutTrivia().Text
            Type = node.Type |> ParserUtil.parseType
            Prop.Get = getStatements 
            Prop.Set = setStatements
            Access = access
        }

    member this.VisitUsingDirective (node:UsingDirectiveSyntax ) =
        
        let trivia = CSharpStatementWalker.GetLeadingTrivia node 
        { UsingNamespace = node.Name.WithoutTrivia().ToFullString(); Trivia = trivia }

    member this.MergeFsharpSyntax (tree, result) = 
        match tree with 
        | None -> result |> Some
        | Some tree -> 
            match tree, result with  
            | other, File (FileWithUsingNamespaceAttributeAndDefault (u, ns, a, s))
            | File (FileWithUsingNamespaceAttributeAndDefault (u, ns, a, s)), other -> 
                let file = 
                    match other with 
                    | UsingStatement u' -> FileWithUsingNamespaceAttributeAndDefault (u @ [u'], ns, a, s)
                    | Namespace n' -> FileWithUsingNamespaceAttributeAndDefault (u, ns @ [n'], a, s)
                    | RootAttributes a' -> FileWithUsingNamespaceAttributeAndDefault (u, ns, a @ a', s)
                    | Structures s' -> FileWithUsingNamespaceAttributeAndDefault (u, ns, a, s @ s')
                    | File (FileWithUsingNamespaceAttributeAndDefault (u', ns', a', s')) -> 
                        FileWithUsingNamespaceAttributeAndDefault (u @ u', ns @ ns', a @ a', s @ s') 
                file |> File |> Some

            | UsingStatement using, Structures s
            | Structures s, UsingStatement using -> 
                FileWithUsingNamespaceAttributeAndDefault ([using], [], [], s) |> File |> Some

            | UsingStatement using, Namespace ns
            | Namespace ns, UsingStatement using -> 
                FileWithUsingNamespaceAttributeAndDefault ([using], [ns], [], []) |> File |> Some

            | Structures s, Namespace ns
            | Namespace ns, Structures s -> 
                FileWithUsingNamespaceAttributeAndDefault ([], [ns], [], s) |> File |> Some

            | Namespace ns', RootAttributes a
            | RootAttributes a, Namespace ns' -> 
                FileWithUsingNamespaceAttributeAndDefault ([], [ns'], a, []) |> File |> Some

            | UsingStatement using, RootAttributes a
            | RootAttributes a, UsingStatement using -> 
                FileWithUsingNamespaceAttributeAndDefault ([using], [], a, []) |> File |> Some

            | Structures s, RootAttributes a
            | RootAttributes a, Structures s -> 
                FileWithUsingNamespaceAttributeAndDefault ([], [], a, s) |> File |> Some

            | RootAttributes a, RootAttributes a' -> FileWithUsingNamespaceAttributeAndDefault ([], [], a @ a', []) |> File |> Some
            | Namespace ns, Namespace ns' -> FileWithUsingNamespaceAttributeAndDefault ([], [ns; ns'], [], []) |> File |> Some
            | Structures s, Structures s' -> Structures (s @ s') |> Some
            | UsingStatement using1, UsingStatement using2 -> FileWithUsingNamespaceAttributeAndDefault ([using1; using2], [], [], []) |> File |> Some


    member this.ParseSyntax tree (x: SyntaxNode) = 

        let fieldToClass fields = {Class.Empty() with Fields = Seq.toList fields}
        let propertyToClass p = {Class.Empty() with Properties = [p]}
        let methodToClass m = {Class.Empty() with Methods = [m]}
    
        let result = 
            match x with
            | :? UsingDirectiveSyntax as x -> x |> this.VisitUsingDirective |> UsingStatement
            | :? NamespaceDeclarationSyntax as x -> x |> this.VisitNamespaceDeclaration |> Namespace
            | :? MethodDeclarationSyntax as x -> x |> this.VisitMethodDeclaration |> methodToClass |> C |> List.singleton |> Structures
            | :? InterfaceDeclarationSyntax as x -> x |> this.VisitInterfaceDeclaration |> Interface |> List.singleton |> Structures
            | :? ClassDeclarationSyntax as x -> x |> this.VisitClassDeclaration |> Structures
            | :? FieldDeclarationSyntax as x -> x |> this.VisitFieldDeclaration |> fieldToClass |> C |> List.singleton |> Structures
            | :? PropertyDeclarationSyntax as x -> x |> this.VisitPropertyDeclaration |>  propertyToClass |> C |> List.singleton |> Structures
            | :? EnumDeclarationSyntax as x -> x |> this.VisitEnumDeclaration |> E |> List.singleton |> Structures
            | :? AttributeListSyntax as x -> x |> this.VisitAttributeListSyntax |> RootAttributes
            //| x -> printfn "Skipping element: %A" <| x.Kind(); Empty

        this.MergeFsharpSyntax (tree, result)
            