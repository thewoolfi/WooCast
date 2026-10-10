Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Окно-перекрытие: накрывает тестовое окно целиком.
$cover = New-Object System.Windows.Forms.Form
$cover.Text = 'Cover Window'
$cover.ClientSize = New-Object System.Drawing.Size(1100, 800)
$cover.BackColor = [System.Drawing.Color]::FromArgb(0, 160, 0)
$cover.StartPosition = 'Manual'
$cover.Left = 60
$cover.Top = 60
$cover.TopMost = $true

$label = New-Object System.Windows.Forms.Label
$label.Text = 'ПЕРЕКРЫТИЕ'
$label.Font = New-Object System.Drawing.Font('Arial', 48, [System.Drawing.FontStyle]::Bold)
$label.ForeColor = [System.Drawing.Color]::Yellow
$label.AutoSize = $true
$label.Location = New-Object System.Drawing.Point(120, 300)
$cover.Controls.Add($label)

[void]$cover.ShowDialog()
