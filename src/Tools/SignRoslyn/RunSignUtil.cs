﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using static SignRoslyn.PathUtil;

namespace SignRoslyn
{
    internal sealed class RunSignUtil
    {
        internal static readonly StringComparer FilePathComparer = StringComparer.OrdinalIgnoreCase;

        private readonly SignData _signData;
        private readonly SignTool _signTool;
        private readonly ContentUtil _contentUtil = new ContentUtil();

        internal RunSignUtil(SignTool signTool, SignData signData)
        {
            _signTool = signTool;
            _signData = signData;
        }

        internal void Go()
        {
            // First validate our inputs and give a useful error message about anything that happens to be missing
            // from the binaries directory.
            var vsixDataMap = VerifyBeforeSign();

            // Next remove public sign from all of the assemblies.  It can interfere with the signing process.
            RemovePublicSign();

            // Next step is to sign all of the assemblies.
            SignAssemblies();

            // Last we sign the VSIX files (being careful to take into account nesting)
            SignVsixes(vsixDataMap);

            // Validate the signing worked and produced actual signed binaries in all locations.
            VerifyAfterSign(vsixDataMap);
        }

        private void RemovePublicSign()
        {
            Console.WriteLine("Removing public sign");
            foreach (var name in _signData.AssemblyNames)
            {
                Console.WriteLine($"\t{name}");
                _signTool.RemovePublicSign(name.FullPath);
            }
        }

        /// <summary>
        /// Sign all of the assembly files.  No need to consider nesting here and it can be done in a single pass.
        /// </summary>
        private void SignAssemblies()
        {
            Console.WriteLine("Signing assemblies");
            foreach (var name in _signData.AssemblyNames)
            {
                Console.WriteLine($"\t{name.RelativePath}");
            }

            _signTool.Sign(_signData.AssemblyNames.Select(x => _signData.BinarySignDataMap[x]));
        }

        /// <summary>
        /// Sign all of the VSIX files.  It is possible for VSIX to nest other VSIX so we must consider this when 
        /// picking the order.
        /// </summary>
        private void SignVsixes(Dictionary<BinaryName, VsixData> vsixDataMap)
        {
            var round = 0;
            var signedSet = new HashSet<BinaryName>(_signData.AssemblyNames);
            var toSignList = _signData.VsixNames.ToList();
            do
            {
                Console.WriteLine($"Signing VSIX round {round}");
                var list = new List<BinaryName>();
                var i = 0;
                var progress = false;
                while (i < toSignList.Count)
                {
                    var vsixName = toSignList[i];
                    var vsixData = vsixDataMap[vsixName];
                    var areNestedBinariesSigned = vsixData.NestedBinaryParts.All(x => signedSet.Contains(x.BinaryName));
                    if (areNestedBinariesSigned)
                    {
                        list.Add(vsixName);
                        toSignList.RemoveAt(i);
                        Console.WriteLine($"\tRepacking {vsixName}");
                        Repack(vsixData);
                        progress = true;
                    }
                    else
                    {
                        i++;
                    }
                }

                if (!progress)
                {
                    throw new Exception("No progress made on nested VSIX which indicates validation bug");
                }

                Console.WriteLine($"\tSigning ...");
                _signTool.Sign(list.Select(x => _signData.BinarySignDataMap[x]));

                // Signing is complete so now we can update the signed set.
                list.ForEach(x => signedSet.Add(x));

                round++;
            } while (toSignList.Count > 0);
        }

        /// <summary>
        /// Repack the VSIX with the signed parts from the binaries directory.
        /// </summary>
        private void Repack(VsixData vsixData)
        {
            using (var package = Package.Open(vsixData.Name.FullPath, FileMode.Open, FileAccess.ReadWrite))
            {
                foreach (var part in package.GetParts())
                {
                    var relativeName = GetPartRelativeFileName(part);
                    var vsixPart = vsixData.GetNestedBinaryPart(relativeName);
                    if (!vsixPart.HasValue)
                    {
                        continue;
                    }

                    using (var stream = File.OpenRead(vsixPart.Value.BinaryName.FullPath))
                    using (var partStream = part.GetStream(FileMode.Open, FileAccess.ReadWrite))
                    {
                        stream.CopyTo(partStream);
                        partStream.SetLength(stream.Length);
                    }
                }
            }
        }

        /// <summary>
        /// Get the name of all VSIX which are nested inside this VSIX.
        /// </summary>
        private IEnumerable<string> GetNestedVsixRelativeNames(BinaryName vsixName)
        {
            return GetVsixPartRelativeNames(vsixName).Where(x => IsVsix(x));
        }

        private bool AreNestedVsixSigned(BinaryName vsixName, HashSet<string> signedSet)
        {
            foreach (var relativeName in GetNestedVsixRelativeNames(vsixName))
            {
                var name = Path.GetFileName(relativeName);
                if (!signedSet.Contains(name))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Return all the assembly and VSIX contents nested in the VSIX
        /// </summary>
        private List<string> GetVsixPartRelativeNames(BinaryName vsixName)
        {
            var list = new List<string>();
            using (var package = Package.Open(vsixName.FullPath, FileMode.Open, FileAccess.Read))
            {
                foreach (var part in package.GetParts())
                {
                    var name = GetPartRelativeFileName(part);
                    list.Add(name);
                }
            }

            return list;
        }

        private Dictionary<BinaryName, VsixData> VerifyBeforeSign()
        {
            var allGood = true;
            var map = VerifyBinariesBeforeSign(ref allGood);
            var vsixDataMap = VerifyVsixContentsBeforeSign(map, ref allGood);

            if (!allGood)
            {
                throw new Exception("Errors validating the state before signing");
            }

            return vsixDataMap;
        }

        /// <summary>
        /// Validate all of the binaries which are specified to be signed exist on disk.  Compute their
        /// checksums at this time so we can use it for VSIX content validation.
        /// </summary>
        private Dictionary<string, BinaryName> VerifyBinariesBeforeSign(ref bool allGood)
        {
            var checksumToNameMap = new Dictionary<string, BinaryName>(StringComparer.Ordinal);
            foreach (var binaryName in _signData.BinaryNames)
            {
                if (!File.Exists(binaryName.FullPath))
                {
                    Console.WriteLine($"Did not find {binaryName} at {binaryName.FullPath}");
                    allGood = false;
                    continue;
                }

                var checksum = _contentUtil.GetChecksum(binaryName.FullPath);
                checksumToNameMap[checksum] = binaryName;
            }

            return checksumToNameMap;
        }

        private Dictionary<BinaryName, VsixData> VerifyVsixContentsBeforeSign(Dictionary<string, BinaryName> checksumToNameMap, ref bool allGood)
        {
            var vsixDataMap = new Dictionary<BinaryName, VsixData>();
            foreach (var vsixName in _signData.VsixNames)
            {
                var data = VerifyVsixContentsBeforeSign(vsixName, checksumToNameMap, ref allGood);
                vsixDataMap[vsixName] = data;
            }

            return vsixDataMap;
        }

        private VsixData VerifyVsixContentsBeforeSign(BinaryName vsixName, Dictionary<string, BinaryName> checksumToNameMap, ref bool allGood)
        {
            var nestedExternalBinaries = new List<string>();
            var nestedParts = new List<VsixPart>();
            using (var package = Package.Open(vsixName.FullPath, FileMode.Open, FileAccess.Read))
            {
                foreach (var part in package.GetParts())
                {
                    var relativeName = GetPartRelativeFileName(part);
                    var name = Path.GetFileName(relativeName);
                    if (!IsVsix(name) && !IsAssembly(name))
                    {
                        continue;
                    }

                    if (_signData.ExternalBinaryNames.Contains(name))
                    {
                        nestedExternalBinaries.Add(name);
                        continue;
                    }

                    if (!_signData.BinaryNames.Any(x => FilePathComparer.Equals(x.Name, name)))
                    {
                        allGood = false;
                        Console.WriteLine($"VSIX {vsixName} has part {name} which is not listed in the sign or external list");
                        continue;
                    }

                    // This represents a binary that we need to sign.  Ensure the content in the VSIX is the same as the 
                    // content in the binaries directory by doing a chekcsum match.
                    using (var stream = part.GetStream())
                    {
                        string checksum = _contentUtil.GetChecksum(stream);
                        BinaryName checksumName;
                        if (!checksumToNameMap.TryGetValue(checksum, out checksumName))
                        {
                            allGood = false;
                            Console.WriteLine($"{vsixName} has part {name} which does not match the content in the binaries directory");
                            continue;
                        }

                        if (!FilePathComparer.Equals(checksumName.Name, name))
                        {
                            allGood = false;
                            Console.WriteLine($"{vsixName} has part {name} with a different name in the binaries directory: {checksumName}");
                            continue;
                        }

                        nestedParts.Add(new VsixPart(relativeName, checksumName));
                    }
                }
            }

            return new VsixData(vsixName, nestedParts.ToImmutableArray(), nestedExternalBinaries.ToImmutableArray());
        }

        private static string GetPartRelativeFileName(PackagePart part)
        {
            var path = part.Uri.OriginalString;
            if (!string.IsNullOrEmpty(path) && path[0] == '/')
            {
                path = path.Substring(1);
            }

            return path;
        }

        private void VerifyAfterSign(Dictionary<BinaryName, VsixData> vsixData)
        {
            if (!VerifyAssembliesAfterSign() || !VerifyVsixContentsAfterSign(vsixData))
            {
                throw new Exception("Verification of signed binaries failed");
            }
        }

        private bool VerifyAssembliesAfterSign()
        {
            var allGood = true;
            foreach (var name in _signData.AssemblyNames)
            {
                using (var stream = File.OpenRead(name.FullPath))
                {
                    if (!_signTool.VerifySignedAssembly(stream))
                    {
                        allGood = false;
                        Console.WriteLine($"Assembly {name.RelativePath} is not signed properly");
                    }
                }
            }

            return allGood;
        }

        private bool VerifyVsixContentsAfterSign(Dictionary<BinaryName, VsixData> vsixDataMap)
        {
            var allGood = true;
            foreach (var vsixName in _signData.VsixNames)
            {
                var vsixData = vsixDataMap[vsixName];
                using (var package = Package.Open(vsixName.FullPath, FileMode.Open, FileAccess.Read))
                {
                    foreach (var part in package.GetParts())
                    {
                        var relativeName = GetPartRelativeFileName(part);
                        var vsixPart = vsixData.GetNestedBinaryPart(relativeName);
                        if (!vsixPart.HasValue || !vsixPart.Value.BinaryName.IsAssembly)
                        {
                            continue;
                        }

                        using (var stream = part.GetStream())
                        {
                            if (!_signTool.VerifySignedAssembly(stream))
                            {
                                allGood = false;
                                Console.WriteLine($"Vsix {vsixName.RelativePath} has part {relativeName} which is not signed.");
                            }
                        }
                    }
                }
            }
            return allGood;
        }

    }
}