# ConsoleDumper

A tool to dump console stdin/out/err

## Description

Assuming that a.exe launches b.exe and the stdio of b.exe is redirected, then the stdin/out/err of b.exe are all invisible. This tool will help you dump all the stdio of b.exe. It will launch a remote console and display all stdio to the remote console.

Compile the ConsoleDumper using Visual Studio 2022 with .NET workload. Rename ConsoleDumper.exe to b.exe and put it into the directory containing b.exe. Rename original b.exe to b.x.exe. 

## Customization

The default mode of ConsoleDumper is to directly forward all stdio. Perhaps you need to process the data stream, in which case please refer to the following content.

ConsoleDumper includes a simple packet filtering architecture, divided into four interfaces: IStreamReader, IStreamSplitter, IPacketFilter, and IStreamWriter.

- IStreamReader: Read the raw buffer from the input stdio.
- IStreamSplitter: Split the incremental input raw buffer and the existing raw buffer into data packets in the specified format.
- IPacketFilter: Modify, block, or insert new packets into the separated data packets.
- IStreamWriter: Write the final generated data packets to the output stdio.

The project comes with some simple examples of implementations of these interfaces. You can choose to use existing implementations or create your own implementations.
