Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
function New-LabWindowsAnswerXml {
    param([Parameter(Mandatory=$true)][ValidateRange(1,64)][int]$ImageIndex,
          [Parameter(Mandatory=$true)][string]$GuestPassword)
    if($GuestPassword.Length -lt 20 -or $GuestPassword.Length -gt 128){throw 'Lab guest password length'}
    $password=[Security.SecurityElement]::Escape($GuestPassword)
    # The only disk exposed by the lab policy is the standalone guest qcow2.
    # This XML is data for guest Windows Setup, never executed by the host.
    return @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
  <settings pass="windowsPE">
    <component name="Microsoft-Windows-International-Core-WinPE" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <SetupUILanguage><UILanguage>en-US</UILanguage></SetupUILanguage>
      <InputLocale>en-US</InputLocale><SystemLocale>en-US</SystemLocale><UILanguage>en-US</UILanguage><UserLocale>en-US</UserLocale>
    </component>
    <component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <DiskConfiguration><Disk wcm:action="add"><DiskID>0</DiskID><WillWipeDisk>true</WillWipeDisk>
        <CreatePartitions>
          <CreatePartition wcm:action="add"><Order>1</Order><Type>Primary</Type><Size>550</Size></CreatePartition>
          <CreatePartition wcm:action="add"><Order>2</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
        </CreatePartitions>
        <ModifyPartitions>
          <ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Active>true</Active><Format>NTFS</Format><Label>System</Label></ModifyPartition>
          <ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>2</PartitionID><Format>NTFS</Format><Label>UntamedLab</Label><Letter>C</Letter></ModifyPartition>
        </ModifyPartitions>
      </Disk><WillShowUI>OnError</WillShowUI></DiskConfiguration>
      <ImageInstall><OSImage><InstallFrom><MetaData wcm:action="add"><Key>/IMAGE/INDEX</Key><Value>$ImageIndex</Value></MetaData></InstallFrom>
        <InstallTo><DiskID>0</DiskID><PartitionID>2</PartitionID></InstallTo><WillShowUI>OnError</WillShowUI>
      </OSImage></ImageInstall>
      <UserData><AcceptEula>true</AcceptEula><FullName>Untamed Lab</FullName><Organization>Isolated testing</Organization></UserData>
      <DynamicUpdate><Enable>false</Enable><WillShowUI>Never</WillShowUI></DynamicUpdate>
    </component>
  </settings>
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <ComputerName>UNTAMED-LAB</ComputerName><TimeZone>Central Europe Standard Time</TimeZone>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <InputLocale>en-US</InputLocale><SystemLocale>en-US</SystemLocale><UILanguage>en-US</UILanguage><UserLocale>en-US</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <OOBE><HideEULAPage>true</HideEULAPage><HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE><ProtectYourPC>3</ProtectYourPC></OOBE>
      <UserAccounts><AdministratorPassword><Value>$password</Value><PlainText>true</PlainText></AdministratorPassword></UserAccounts>
      <AutoLogon><Password><Value>$password</Value><PlainText>true</PlainText></Password><Enabled>true</Enabled><LogonCount>2</LogonCount><Username>Administrator</Username></AutoLogon>
    </component>
  </settings>
</unattend>
"@
}
function Read-LabAnswerXml {
    param([byte[]]$Bytes)
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver=$null;$settings.MaxCharactersInDocument=131072
    $memory=[IO.MemoryStream]::new($Bytes,$false)
    $reader=[Xml.XmlReader]::Create($memory,$settings)
    try{
        $document=[Xml.XmlDocument]::new();$document.XmlResolver=$null;$document.Load($reader)
        if($document.DocumentElement.LocalName -ne 'unattend' -or $document.DocumentElement.NamespaceURI -ne 'urn:schemas-microsoft-com:unattend'){throw 'Invalid lab answer XML root'}
        return $document
    }finally{$reader.Dispose();$memory.Dispose()}
}
Export-ModuleMember -Function New-LabWindowsAnswerXml,Read-LabAnswerXml
