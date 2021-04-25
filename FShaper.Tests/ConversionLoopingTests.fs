﻿namespace Tests

open NUnit.Framework
open FShaper.Core
open FsUnit
open CodeFormatter

[<TestFixture>]
type LoopingTests () =

    [<Test>]
    member this.``standard incrementing for loop i, i < i++`` () = 
        let csharp = 
             """for (int n = 0; n < 10; n++) 
                {
                    Console.WriteLine($"{n}");
                }"""

        let fsharp = 
             """for n = 0 to 9 do
                    Console.WriteLine(sprintf "%O" (n))"""
                   
        csharp
        |> reduceIndent
        |> Converter.run 
        |> logConverted
        |> should equal (formatFsharp fsharp)

    [<Test>]
    member this.``standard incrementing for loop i, i <= i++`` () = 
        let csharp = 
             """for (int n = 0; n <= 10; n++) 
                {
                    Console.WriteLine($"{n}");
                }"""

        let fsharp = 
             """for n = 0 to 10 do
                    Console.WriteLine(sprintf "%O" (n))"""
                   
        csharp
        |> reduceIndent
        |> Converter.run 
        |> logConverted
        |> should equal (formatFsharp fsharp)

    [<Test>]
    member this.``standard decrementing for loop i, i < i--`` () = 
        let csharp = 
             """for (int n = 10; n > 0; n--) 
                {
                    Console.WriteLine($"{n}");
                }"""

        let fsharp = 
             """for n = 10 downto 1 do
                    Console.WriteLine(sprintf "%O" (n))"""
                   
        csharp
        |> reduceIndent
        |> Converter.run 
        |> logConverted
        |> should equal (formatFsharp fsharp)

    [<Test>]
    member this.``weird for loop`` () = 
        let csharp = 
             """void Foo()
                {
                    int i, j;
                    long[] c = new long[100];
                    for (c[i = 1] = 1L; i < n; c[0] = -c[0], i++)
                    {
                        Console.WriteLine($"{i}");
                    }
                }"""

        let fsharp = 
             """member this.Foo() =
                    let mutable i = Unchecked.defaultof<int>
                    let mutable j = Unchecked.defaultof<int>
                    let mutable c = Array.zeroCreate<int64> (100)
                    i <- 1
                    c.[i] <- 1L
                    for i = 1 to (n - 1) do
                        Console.WriteLine(sprintf "%O" (i))
                        c.[0] <- -c.[0]"""
                   
        csharp
        |> reduceIndent
        |> Converter.run 
        |> logConverted
        |> should equal (formatFsharpWithClass fsharp)

    [<Test>]
    member this.``weird double for loop`` () = 
        let csharp = 
             """void coef(int n)
                {
                    int i, j;

                    if (n < 0 || n > 63) System.Environment.Exit(0);

                    for (c[i = 0] = 1L; i < n; c[0] = -c[0], i++)
                        for (c[1 + (j = i)] = 1L; j > 0; j--)
                            c[j] = c[j - 1] - c[j];
                }"""

        let fsharp = 
             """member this.coef (n: int) =
                    let mutable i = Unchecked.defaultof<int>
                    let mutable j = Unchecked.defaultof<int>
                    if n < 0 || n > 63 then System.Environment.Exit(0)
                    i <- 0
                    c.[i] <- 1L
                    for i = 0 to (n - 1) do
                        j <- i
                        c.[1 + j] <- 1L
                        for j = i downto 1 do
                            c.[j] <- c.[j - 1] - c.[j]
                        c.[0] <- -c.[0]"""
                   
        csharp
        |> reduceIndent
        |> Converter.run 
        |> logConverted
        |> should equal (formatFsharpWithClass fsharp)

    [<Test>]
    member this.``convert ushort cast`` () = 
        let csharp = 
             """public void Main() {
                    for (ushort ctr = (ushort)'a'; ctr <= (ushort) 'z'; ctr++)
                        sb.Append(Convert.ToChar(ctr), 4); 
                }"""
    
        let fsharp = 
             """member this.Main() =
                    for ctr = (int 'a') to (int 'z') do
                        sb.Append(Convert.ToChar(ctr), 4)"""

        csharp
        |> reduceIndent
        |> Converter.run 
        |> logConverted
        |> should equal (formatFsharpWithClass fsharp)

    [<Test>]
    member this.``simple while loop`` () = 
        let csharp = 
             """void Loop()
                {
                    int i = 10;
                    while (i >= 1)
                    {
                        i--;
                        Console.WriteLine($"{i}");
                    }
                }"""

        let fsharp = 
             """member this.Loop() =
                    let mutable i = 10
                    while i >= 1 do
                        i <- i - 1
                        Console.WriteLine(sprintf "%O" (i))"""
                   
        csharp
        |> reduceIndent
        |> Converter.run 
        |> logConverted
        |> should equal (formatFsharpWithClass fsharp)

    [<Test>]
    member this.``simple while loop with pre-increment`` () = 
        let csharp = 
             """void Loop()
                {
                    while (--i >= 1)
                        Console.WriteLine($"{i}");
                }"""

        let fsharp = 
             """member this.Loop() =
                    i <- i - 1
                    while i >= 1 do
                        Console.WriteLine(sprintf "%O" (i))
                        i <- i - 1"""
                   
        csharp
        |> reduceIndent
        |> Converter.run 
        |> logConverted
        |> should equal (formatFsharpWithClass fsharp)

    [<Test>]
    member this.``simple while loop with post-increment`` () = 
        let csharp = 
             """void Loop()
                {
                    while (i-- >= 1)
                        Console.WriteLine($"{i}");
                }"""

        let fsharp = 
             """member this.Loop() =
                    while i >= 1 do
                        i <- i - 1
                        Console.WriteLine(sprintf "%O" (i))"""
                   
        csharp
        |> reduceIndent
        |> Converter.run 
        |> logConverted
        |> should equal (formatFsharpWithClass fsharp)

    [<Test>]
    member this.``for loop with long ident for condition and pre-increment`` () = 
        let csharp = 
             """void Foo() 
                {
                    for(int i = 0; i < word.Length; ++i)
                    {
                        Console.WriteLine($"{i}");
                    }
                }"""

        let fsharp = 
             """member this.Foo() =
                    for i = 0 to (word.Length - 1) do
                        Console.WriteLine(sprintf "%O" (i))"""
                   
        csharp
        |> reduceIndent
        |> Converter.run 
        |> logConverted
        |> should equal (formatFsharpWithClass fsharp)

    [<Test>]
    member this.``for loop with <= in loop`` () = 
        let csharp = 
             """void Foo() 
                {
                    for(int i = 1; i <= word.Length; ++i)
                    {
                        Console.WriteLine($"{i - 1}");
                    }
                }"""

        let fsharp = 
             """member this.Foo() =
                    for i = 1 to word.Length do
                        Console.WriteLine(sprintf "%O" (i - 1))"""
                   
        csharp
        |> reduceIndent
        |> Converter.run 
        |> logConverted
        |> should equal (formatFsharpWithClass fsharp)
