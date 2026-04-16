param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression

function Get-ColumnIndex {
    param([string]$Letters)

    $value = 0
    foreach ($ch in $Letters.ToUpperInvariant().ToCharArray()) {
        $value = ($value * 26) + ([int][char]$ch - [int][char]'A' + 1)
    }
    return $value
}

function Get-CellParts {
    param([string]$Ref)

    if ($Ref -notmatch '^([A-Z]+)(\d+)$') {
        throw "Invalid cell reference: $Ref"
    }

    [pscustomobject]@{
        Column = Get-ColumnIndex $Matches[1]
        Row = [int]$Matches[2]
    }
}

function Convert-TimeRange {
    param([string]$Text)

    if ($Text -notmatch '(\d{1,2})\s*пара\s*(\d{3,4})\s*-\s*(\d{3,4})') {
        return $null
    }

    function Format-TimePart([string]$raw) {
        $raw = $raw.PadLeft(4, '0')
        return "$($raw.Substring(0, 2)):$($raw.Substring(2, 2))"
    }

    return "$(Format-TimePart $Matches[2])-$(Format-TimePart $Matches[3])"
}

function Normalize-Subject {
    param([string]$Text)

    $normalized = ($Text -replace '\s+', ' ').Trim()
    $normalized = $normalized -replace '\s*,\s*\(', ' ('
    $normalized = $normalized -replace '\(\s+', '('
    $normalized = $normalized -replace '\s+\)', ')'

    $typePattern = 'лекц|практ|лаб|семинар|зач[её]т|экзам|консультац|практик'
    if ($normalized -match "^(?<name>.+?)\s*\((?<kind>[^)]*(?:$typePattern)[^)]*)\)") {
        $name = ($Matches['name'] -replace '\s*,\s*$', '').Trim()
        $name = ($name -replace ',\s*[А-ЯЁ][а-яё-]+\s+[А-ЯЁ]\.[А-ЯЁ]\.?$', '').Trim()
        $kind = $Matches['kind'].Trim()
        return "$name ($kind)"
    }

    if ($normalized.Contains(',')) {
        return $normalized.Split(',')[0].Trim()
    }

    return $normalized
}

function Get-DirectionKey {
    param([string]$Code)

    switch ($Code) {
        '01.03.02' { return 'pmi' }
        '09.03.01' { return 'ivt' }
        '44.03.05' { return 'ped' }
        default { return ($Code -replace '[^0-9]+', '-') }
    }
}

function Get-ShortTitle {
    param([string]$Code)

    switch ($Code) {
        '01.03.02' { return 'ПМИ' }
        '09.03.01' { return 'ИВТ' }
        '44.03.05' { return 'Педагогическое образование' }
        default { return $Code }
    }
}

function Parse-Range {
    param([string]$Ref)

    $parts = $Ref.Split(':')
    $start = Get-CellParts $parts[0]
    $end = if ($parts.Count -gt 1) { Get-CellParts $parts[1] } else { $start }

    [pscustomobject]@{
        Ref = $Ref
        StartColumn = $start.Column
        EndColumn = $end.Column
        StartRow = $start.Row
        EndRow = $end.Row
    }
}

function Get-CellValue {
    param($Cell, [string[]]$SharedStrings)

    if ($null -eq $Cell) {
        return ''
    }

    if ($Cell.t -eq 's') {
        $index = [int]$Cell.v
        if ($index -ge 0 -and $index -lt $SharedStrings.Count) {
            return $SharedStrings[$index]
        }
        return ''
    }

    if ($Cell.t -eq 'inlineStr') {
        return $Cell.is.InnerText
    }

    if ($null -ne $Cell.v) {
        return [string]$Cell.v
    }

    return ''
}

function Get-DayNumber {
    param([string]$Day)

    switch -Regex ($Day) {
        '^Понедельник$' { return 1 }
        '^Вторник$' { return 2 }
        '^Среда$' { return 3 }
        '^Четверг$' { return 4 }
        '^Пятница$' { return 5 }
        '^Суббота$' { return 6 }
        '^Воскресенье$' { return 7 }
        default { return $null }
    }
}

$stream = [System.IO.File]::Open($SourcePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
$zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read)

try {
    $sharedStrings = @()
    $sharedEntry = $zip.GetEntry('xl/sharedStrings.xml')
    if ($sharedEntry) {
        $reader = [System.IO.StreamReader]::new($sharedEntry.Open())
        [xml]$sharedXml = $reader.ReadToEnd()
        $reader.Close()

        $sharedNs = [System.Xml.XmlNamespaceManager]::new($sharedXml.NameTable)
        $sharedNs.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        foreach ($si in $sharedXml.SelectNodes('//x:si', $sharedNs)) {
            $sharedStrings += $si.InnerText
        }
    }

    $reader = [System.IO.StreamReader]::new($zip.GetEntry('xl/workbook.xml').Open())
    [xml]$workbook = $reader.ReadToEnd()
    $reader.Close()

    $reader = [System.IO.StreamReader]::new($zip.GetEntry('xl/_rels/workbook.xml.rels').Open())
    [xml]$relationships = $reader.ReadToEnd()
    $reader.Close()

    $workbookNs = [System.Xml.XmlNamespaceManager]::new($workbook.NameTable)
    $workbookNs.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
    $workbookNs.AddNamespace('r', 'http://schemas.openxmlformats.org/officeDocument/2006/relationships')

    $groups = @{}

    foreach ($sheet in $workbook.SelectNodes('//x:sheets/x:sheet', $workbookNs)) {
        $relationshipId = $sheet.GetAttribute('id', 'http://schemas.openxmlformats.org/officeDocument/2006/relationships')
        $relationship = $relationships.Relationships.Relationship | Where-Object { $_.Id -eq $relationshipId } | Select-Object -First 1
        $target = if ($relationship.Target.StartsWith('/')) { $relationship.Target.TrimStart('/') } else { "xl/$($relationship.Target)" }
        $target = $target -replace '\\', '/'

        $reader = [System.IO.StreamReader]::new($zip.GetEntry($target).Open())
        [xml]$worksheet = $reader.ReadToEnd()
        $reader.Close()

        $sheetNs = [System.Xml.XmlNamespaceManager]::new($worksheet.NameTable)
        $sheetNs.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')

        $cells = @{}
        $cellRanges = @{}
        foreach ($cell in $worksheet.SelectNodes('//x:sheetData/x:row/x:c', $sheetNs)) {
            $value = Get-CellValue $cell $sharedStrings
            if ([string]::IsNullOrWhiteSpace($value)) {
                continue
            }

            $parts = Get-CellParts $cell.r
            $cells[$cell.r] = ($value -replace "`r?`n", ' ').Trim()
            $cellRanges[$cell.r] = [pscustomobject]@{
                Ref = $cell.r
                StartColumn = $parts.Column
                EndColumn = $parts.Column
                StartRow = $parts.Row
                EndRow = $parts.Row
            }
        }

        if ($worksheet.worksheet.mergeCells) {
            foreach ($mergeCell in $worksheet.SelectNodes('//x:mergeCells/x:mergeCell', $sheetNs)) {
                $range = Parse-Range $mergeCell.ref
                $topLeft = ($mergeCell.ref.Split(':')[0])
                if ($cellRanges.ContainsKey($topLeft)) {
                    $cellRanges[$topLeft] = $range
                }
            }
        }

        $courseText = ($cells.Values | Where-Object { $_ -match '^Курс' } | Select-Object -First 1)
        if ($courseText -notmatch '(\d+)') {
            continue
        }
        $course = [int]$Matches[1]

        $directionText = ($cells.Values | Where-Object { $_ -match '^Направление' } | Select-Object -First 1)
        if ($directionText -notmatch 'Направление\s+(?<code>\d{2}\.\d{2}\.\d{2})\s+(?<name>.+)$') {
            continue
        }

        $directionCode = $Matches['code'].Trim()
        $directionName = ($Matches['name'] -replace '\s+', ' ').Trim()
        $directionKey = Get-DirectionKey $directionCode
        $shortTitle = Get-ShortTitle $directionCode
        $groupId = "$directionKey-$course"

        if (-not $groups.ContainsKey($groupId)) {
            $groups[$groupId] = [ordered]@{
                id = $groupId
                directionCode = $directionCode
                directionName = $directionName
                shortTitle = $shortTitle
                title = "$shortTitle, $course курс"
                course = $course
                subGroups = [System.Collections.Generic.List[int]]::new()
                sourceSheets = [System.Collections.Generic.List[string]]::new()
                entries = [System.Collections.Generic.List[object]]::new()
                _entryKeys = [System.Collections.Generic.HashSet[string]]::new()
            }
        }

        $group = $groups[$groupId]
        $group.sourceSheets.Add($sheet.name.Trim()) | Out-Null

        $subGroupByColumn = @{}
        foreach ($item in $cells.GetEnumerator()) {
            if ($item.Value -match '^Подгруппа\s+(\d+)') {
                $parts = Get-CellParts $item.Key
                $subGroup = [int]$Matches[1]
                $subGroupByColumn[$parts.Column] = $subGroup
                if (-not $group.subGroups.Contains($subGroup)) {
                    $group.subGroups.Add($subGroup)
                }
            }
        }

        $rowDay = @{}
        foreach ($item in $cells.GetEnumerator()) {
            $parts = Get-CellParts $item.Key
            if ($parts.Column -ne 1) {
                continue
            }

            $dayNumber = Get-DayNumber $item.Value
            if ($null -eq $dayNumber) {
                continue
            }

            $range = $cellRanges[$item.Key]
            for ($row = $range.StartRow; $row -le $range.EndRow; $row++) {
                $rowDay[$row] = $dayNumber
            }
        }

        $lessonRows = @{}
        foreach ($item in $cells.GetEnumerator()) {
            $parts = Get-CellParts $item.Key
            if ($parts.Column -ne 2 -or $item.Value -notmatch '^(\d+)\s*пара') {
                continue
            }

            $range = $cellRanges[$item.Key]
            $lessonRows[$range.StartRow] = [pscustomobject]@{
                Row = $range.StartRow
                EndRow = [Math]::Max($range.EndRow, $range.StartRow + 1)
                Lesson = [int]$Matches[1]
                Time = Convert-TimeRange $item.Value
                Day = $rowDay[$range.StartRow]
            }
        }

        foreach ($item in $cells.GetEnumerator()) {
            $parts = Get-CellParts $item.Key
            if ($parts.Row -lt 15 -or $parts.Column -lt 3) {
                continue
            }

            $range = $cellRanges[$item.Key]
            $topRow = $range.StartRow
            if (-not $lessonRows.ContainsKey($topRow) -and $lessonRows.ContainsKey($topRow - 1)) {
                $topRow = $topRow - 1
            }

            if (-not $lessonRows.ContainsKey($topRow)) {
                continue
            }

            $lessonInfo = $lessonRows[$topRow]
            if ($null -eq $lessonInfo.Day) {
                continue
            }

            $weekType = $null
            if (-not ($range.StartRow -le $lessonInfo.Row -and $range.EndRow -ge $lessonInfo.EndRow)) {
                $weekType = if ($range.StartRow -eq $lessonInfo.Row) { 1 } else { 2 }
            }

            $subject = Normalize-Subject $item.Value
            if ([string]::IsNullOrWhiteSpace($subject)) {
                continue
            }

            $subGroups = @()
            if ($subGroupByColumn.Count -gt 0) {
                foreach ($column in $subGroupByColumn.Keys | Sort-Object) {
                    if ($column -ge $range.StartColumn -and $column -le $range.EndColumn) {
                        $subGroups += $subGroupByColumn[$column]
                    }
                }

                if ($subGroups.Count -eq 0) {
                    continue
                }
            } else {
                $subGroups += $null
            }

            foreach ($subGroup in $subGroups) {
                $key = "$($lessonInfo.Day)|$($lessonInfo.Lesson)|$($lessonInfo.Time)|$weekType|$subGroup|$subject"
                if (-not $group._entryKeys.Add($key)) {
                    continue
                }

                $group.entries.Add([ordered]@{
                    day = $lessonInfo.Day
                    lesson = $lessonInfo.Lesson
                    time = $lessonInfo.Time
                    weekType = $weekType
                    subGroup = $subGroup
                    subject = $subject
                }) | Out-Null
            }
        }
    }

    $directionOrder = @{
        '01.03.02' = 1
        '09.03.01' = 2
        '44.03.05' = 3
    }

    $resultGroups = foreach ($group in $groups.Values | Sort-Object @{ Expression = { $directionOrder[$_.directionCode] } }, @{ Expression = { $_.course } }) {
        $group.subGroups = @($group.subGroups | Sort-Object)
        $group.sourceSheets = @($group.sourceSheets | Sort-Object)
        $group.entries = @($group.entries |
            ForEach-Object { [pscustomobject]$_ } |
            Sort-Object day, lesson, @{ Expression = { if ($null -eq $_.weekType) { 0 } else { $_.weekType } } }, @{ Expression = { if ($null -eq $_.subGroup) { 0 } else { $_.subGroup } } }, subject)
        $group.Remove('_entryKeys')
        [pscustomobject]$group
    }

    $catalog = [ordered]@{
        semester = '2 семестр 2025-2026'
        faculty = 'Математики и компьютерных наук'
        weekReferenceDate = '2026-04-13'
        weekReferenceType = 1
        groups = @($resultGroups)
    }

    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }

    $json = $catalog | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($OutputPath, $json, [System.Text.UTF8Encoding]::new($false))
}
finally {
    $zip.Dispose()
    $stream.Dispose()
}
