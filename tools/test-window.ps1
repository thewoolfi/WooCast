Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'WooCast Capture Test'
$form.ClientSize = New-Object System.Drawing.Size(800, 600)
$form.BackColor = [System.Drawing.Color]::White
$form.StartPosition = 'Manual'
$form.Left = 120
$form.Top = 120

$label = New-Object System.Windows.Forms.Label
$label.Text = 'WGC TEST'
$label.Font = New-Object System.Drawing.Font('Arial', 56, [System.Drawing.FontStyle]::Bold)
$label.ForeColor = [System.Drawing.Color]::FromArgb(200, 0, 0)
$label.AutoSize = $true
$label.Location = New-Object System.Drawing.Point(60, 60)
$form.Controls.Add($label)

$panel = New-Object System.Windows.Forms.Panel
$panel.BackColor = [System.Drawing.Color]::FromArgb(0, 90, 200)
$panel.Location = New-Object System.Drawing.Point(60, 300)
$panel.Size = New-Object System.Drawing.Size(680, 220)
$form.Controls.Add($panel)

$marker = New-Object System.Windows.Forms.Label
$marker.Text = 'metka'
$marker.Font = New-Object System.Drawing.Font('Arial', 24)
$marker.ForeColor = [System.Drawing.Color]::White
$marker.BackColor = [System.Drawing.Color]::Transparent
$marker.AutoSize = $true
$marker.Location = New-Object System.Drawing.Point(40, 80)
$panel.Controls.Add($marker)

# Системный захват отдаёт кадр, когда содержимое окна меняется: статичное окно
# не даёт ни одного кадра. Поэтому счётчик тикает и обновляет надпись.
$script:tick = 0
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 100
$timer.Add_Tick({
    $script:tick++
    $marker.Text = "tick $script:tick"
})
$timer.Start()

[void]$form.ShowDialog()
