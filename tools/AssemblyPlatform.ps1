# Dot-sourced by Pack-Yak.ps1 and Test-YakPackage.ps1. Reads assembly metadata without loading the assembly.

<# True when the assembly carries an assembly-level TargetPlatform attribute naming Windows (a net8.0-windows build). #>
function Test-WindowsTargetedAssembly([byte[]]$Bytes) {
    $stream = [IO.MemoryStream]::new($Bytes)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        foreach ($handle in $metadata.GetAssemblyDefinition().GetCustomAttributes()) {
            $attribute = $metadata.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
            $member = $metadata.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if ($member.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
            $type = $metadata.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$member.Parent)
            if ($metadata.GetString($type.Name) -cne 'TargetPlatformAttribute') { continue }
            $value = [Text.Encoding]::UTF8.GetString($metadata.GetBlobBytes($attribute.Value))
            if ($value -match 'Windows') { return $true }
        }
        return $false
    } finally { $pe.Dispose(); $stream.Dispose() }
}
