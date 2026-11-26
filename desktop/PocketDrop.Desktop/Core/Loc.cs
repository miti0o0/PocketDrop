using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Markup;

namespace PocketDrop.Core
{
    /// <summary>UI strings in English and Bulgarian. Add a column to support another language.</summary>
    public static class Loc
    {
        public static string Language { get; private set; } = "en";

        public static void Init(string setting)
        {
            Language = setting switch
            {
                "en" or "bg" => setting,
                _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "bg" ? "bg" : "en",
            };
        }

        public static string T(string key)
        {
            if (!Strings.TryGetValue(key, out var pair)) return key;
            return Language == "bg" ? pair.bg : pair.en;
        }

        public static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

        public static string Size(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            var u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
        }

        private static readonly Dictionary<string, (string en, string bg)> Strings = new()
        {
            ["tagline"] = ("Send files from your phone to this PC", "Пращай файлове от телефона към този компютър"),
            ["status.ready"] = ("Ready to receive", "Готов за получаване"),
            ["status.off"] = ("Receiving is off", "Получаването е изключено"),
            ["status.receiving"] = ("Receiving…", "Получаване…"),

            ["section.pc"] = ("This PC", "Този компютър"),
            ["pc.name"] = ("Name shown on the phone", "Име, което се вижда на телефона"),
            ["pc.address"] = ("Network address", "Мрежов адрес"),
            ["pc.noNetwork"] = ("Not connected to a network", "Няма връзка с мрежа"),

            ["section.wifi"] = ("Wi-Fi", "Wi-Fi"),
            ["wifi.enable"] = ("Receive over Wi-Fi", "Получаване през Wi-Fi"),
            ["wifi.hint"] = ("Phone and PC must be on the same Wi-Fi network.", "Телефонът и компютърът трябва да са в една и съща Wi-Fi мрежа."),

            ["section.browser"] = ("iPhone or any phone without the app", "iPhone или телефон без приложението"),
            ["browser.hint"] = ("Scan the code with the camera, or open this address in the phone's browser:", "Сканирай кода с камерата или отвори този адрес в браузъра на телефона:"),

            ["section.remote"] = ("From anywhere (mobile data)", "Отвсякъде (и през мобилни данни)"),
            ["remote.enable"] = ("Allow sending from anywhere over the internet", "Позволи изпращане отвсякъде през интернет"),
            ["remote.hint"] = ("The phone doesn't have to be on your Wi-Fi. Files travel over a secure Cloudflare connection (free, no account). The link is secret and changes every time PocketDrop starts. You still accept every drop here.",
                "Телефонът не трябва да е в твоя Wi-Fi. Файловете минават през защитена връзка на Cloudflare (безплатно, без регистрация). Линкът е таен и се сменя при всяко пускане на PocketDrop. Пак ти потвърждаваш всяко изпращане тук."),
            ["remote.downloading"] = ("Preparing (one-time download from Cloudflare)… {0}%", "Подготовка (еднократно изтегляне от Cloudflare)… {0}%"),
            ["remote.connecting"] = ("Connecting…", "Свързване…"),
            ["remote.ready"] = ("Ready. Scan the code with the phone's camera, or send yourself the link:",
                "Готово. Сканирай кода с камерата на телефона или си прати линка:"),
            ["remote.reconnecting"] = ("Reconnecting… {0}", "Свързване отново… {0}"),
            ["remote.lost"] = ("The connection to Cloudflare was lost.", "Връзката с Cloudflare прекъсна."),
            ["remote.noInternet"] = ("No internet connection.", "Няма интернет връзка."),
            ["remote.timeout"] = ("Cloudflare didn't answer in time.", "Cloudflare не отговори навреме."),
            ["remote.copy"] = ("Copy link", "Копирай линка"),
            ["remote.copied"] = ("Copied ✓", "Копирано ✓"),
            ["remote.newLink"] = ("New link", "Нов линк"),
            ["remote.newLinkTip"] = ("Makes a new link; the old one stops working.", "Прави нов линк; старият спира да работи."),

            ["section.bt"] = ("Bluetooth", "Bluetooth"),
            ["bt.enable"] = ("Receive over Bluetooth", "Получаване през Bluetooth"),
            ["bt.hint"] = ("Pair the phone with this PC once in Windows Bluetooth settings. Bluetooth is slower than Wi-Fi but works without a network.",
                "Сдвои телефона с компютъра веднъж от настройките за Bluetooth в Windows. Bluetooth е по-бавен от Wi-Fi, но работи без мрежа."),
            ["bt.settings"] = ("Bluetooth settings", "Настройки за Bluetooth"),
            ["bt.running"] = ("Bluetooth is on and visible to paired phones.", "Bluetooth е включен и видим за сдвоени телефони."),
            ["bt.noAdapter"] = ("No Bluetooth adapter found on this PC.", "Не е намерен Bluetooth адаптер на този компютър."),
            ["bt.disabled"] = ("Bluetooth receiving is off.", "Получаването през Bluetooth е изключено."),
            ["bt.failed"] = ("Bluetooth could not start: {0}", "Bluetooth не можа да стартира: {0}"),

            ["section.folder"] = ("Receive folder", "Папка за получаване"),
            ["folder.hint"] = ("Every drop gets its own sub-folder here (the phone picks its name), or goes into an existing one.",
                "Всяко изпращане получава собствена подпапка тук (името се избира от телефона) или отива в съществуваща."),
            ["folder.change"] = ("Change…", "Промени…"),
            ["folder.open"] = ("Open", "Отвори"),
            ["folder.pick"] = ("Choose where received files are saved", "Избери къде да се записват получените файлове"),

            ["section.options"] = ("Options", "Опции"),
            ["opt.openExplorer"] = ("Open the folder in File Explorer when a drop finishes", "Отваряй папката във File Explorer, когато приключи"),
            ["opt.autostart"] = ("Start automatically when the PC starts (in the background)",
                "Стартирай автоматично при пускане на компютъра (на заден план)"),
            ["opt.autostartHint"] = ("No window opens: PocketDrop waits in the tray (under ^ next to the clock), ready to receive.",
                "Не се отваря прозорец: PocketDrop чака до часовника (под ^), готов да получава."),
            ["opt.language"] = ("Language (restart to apply)", "Език (след рестарт)"),
            ["opt.lang.auto"] = ("Automatic", "Автоматично"),

            ["section.recent"] = ("Recent drops", "Последни"),
            ["recent.empty"] = ("Nothing received yet.", "Все още нищо не е получено."),
            ["recent.files"] = ("{0} files", "{0} файла"),

            ["tray.open"] = ("Open PocketDrop", "Отвори PocketDrop"),
            ["tray.folder"] = ("Open receive folder", "Отвори папката за получаване"),
            ["tray.quit"] = ("Quit", "Изход"),
            ["tray.hidden"] = ("PocketDrop keeps running in the background. Open it from this icon (under ^) or the desktop shortcut.",
                "PocketDrop продължава да работи на заден план. Отвори го от тази иконка (под ^) или от прекия път на работния плот."),

            ["confirm.title"] = ("Incoming drop", "Входящи файлове"),
            ["confirm.body"] = ("{0} wants to send you {1} ({2}).", "{0} иска да ти изпрати {1} ({2})."),
            ["confirm.oneFile"] = ("1 file", "1 файл"),
            ["confirm.files"] = ("{0} files", "{0} файла"),
            ["confirm.more"] = ("…and {0} more", "…и още {0}"),
            ["confirm.newFolder"] = ("New folder", "Нова папка"),
            ["confirm.existingFolder"] = ("Existing folder", "Съществуваща папка"),
            ["confirm.via"] = ("via {0}", "през {0}"),
            ["confirm.accept"] = ("Accept", "Приеми"),
            ["confirm.decline"] = ("Decline", "Откажи"),

            ["progress.from"] = ("Receiving from {0}", "Получаване от {0}"),
            ["progress.file"] = ("{0} of {1} · {2}", "{0} от {1} · {2}"),
            ["progress.bytes"] = ("{0} of {1}", "{0} от {1}"),
            ["progress.left"] = ("{0} left", "остават {0}"),
            ["progress.done"] = ("Done — {0} saved", "Готово — {0} записани"),
            ["progress.cancelled"] = ("Cancelled", "Отказано"),
            ["progress.failed"] = ("Transfer failed", "Прехвърлянето се провали"),
            ["progress.keptFiles"] = ("{0} files were saved before it stopped.", "{0} файла бяха записани преди прекъсването."),
            ["progress.openFolder"] = ("Open folder", "Отвори папката"),
            ["progress.cancel"] = ("Cancel", "Откажи"),

            ["setup.title"] = ("Welcome to PocketDrop", "Добре дошъл в PocketDrop"),
            ["setup.subtitle"] = ("Two quick questions and you're ready.", "Два бързи въпроса и си готов."),
            ["setup.btQuestion"] = ("Does this PC have Bluetooth?", "Този компютър има ли Bluetooth?"),
            ["setup.btYes"] = ("Yes — use Bluetooth and Wi-Fi", "Да — използвай Bluetooth и Wi-Fi"),
            ["setup.btYesHint"] = ("Works even without a shared network. The phone must be paired with the PC.", "Работи дори без обща мрежа. Телефонът трябва да е сдвоен с компютъра."),
            ["setup.btNo"] = ("No — Wi-Fi only", "Не — само Wi-Fi"),
            ["setup.btNoHint"] = ("Fastest. Phone and PC on the same Wi-Fi network.", "Най-бързо. Телефонът и компютърът в една Wi-Fi мрежа."),
            ["setup.btDetected"] = ("A Bluetooth adapter was found on this PC.", "На този компютър е намерен Bluetooth адаптер."),
            ["setup.btNotDetected"] = ("No Bluetooth adapter was found on this PC.", "На този компютър не е намерен Bluetooth адаптер."),
            ["setup.folderQuestion"] = ("Where should received files go?", "Къде да отиват получените файлове?"),
            ["setup.finish"] = ("Start PocketDrop", "Стартирай PocketDrop"),

            ["update.available"] = ("Version {0} is available", "Налична е версия {0}"),
            ["update.hint"] = ("You have {0}. Updating keeps your settings and folders. Windows asks for permission once.",
                "Имаш {0}. Обновяването запазва настройките и папките. Windows ще поиска разрешение веднъж."),
            ["update.button"] = ("Update", "Обнови"),
            ["update.download"] = ("Download", "Изтегли"),
            ["update.portable"] = ("This is the portable version: download the new one and replace PocketDrop.exe.",
                "Това е преносимата версия: изтегли новата и замени PocketDrop.exe."),
            ["update.downloading"] = ("Downloading… {0}%", "Изтегляне… {0}%"),
            ["update.installing"] = ("Installing — PocketDrop will restart by itself…", "Инсталиране — PocketDrop ще се рестартира сам…"),
            ["update.failed"] = ("Update failed: {0}", "Обновяването не успя: {0}"),
            ["update.cancelled"] = ("The update was cancelled.", "Обновяването беше отказано."),
            ["update.busy"] = ("Wait until the current transfer finishes.", "Изчакай текущото прехвърляне да приключи."),
            ["update.check"] = ("Check for updates", "Провери за обновления"),
            ["update.checking"] = ("Checking…", "Проверка…"),
            ["update.latest"] = ("You have the latest version.", "Имаш най-новата версия."),
            ["update.checkFailed"] = ("Couldn't check for updates. Check the internet connection.", "Не можа да се провери за обновления. Провери интернет връзката."),
            ["update.notConfigured"] = ("This build has no update source (it isn't from a GitHub release).",
                "Тази версия няма източник за обновления (не е от GitHub release)."),
            ["update.version"] = ("Version {0}", "Версия {0}"),
            ["update.tray"] = ("Update to {0}", "Обнови до {0}"),
            ["update.tip"] = ("PocketDrop {0} is available. Click to update.", "Налична е PocketDrop {0}. Кликни, за да обновиш."),
            ["update.done"] = ("PocketDrop was updated to {0}.", "PocketDrop е обновен до {0}."),

            ["error.port"] = ("Port {0} is already in use — is PocketDrop already running?", "Порт {0} вече се използва — PocketDrop да не би вече да работи?"),
            ["error.folder"] = ("The receive folder could not be created: {0}", "Папката за получаване не можа да бъде създадена: {0}"),
        };
    }

    /// <summary>XAML: <c>Text="{core:T confirm.accept}"</c></summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class TExtension : MarkupExtension
    {
        public TExtension() { }
        public TExtension(string key) => Key = key;
        [ConstructorArgument("key")] public string Key { get; set; } = "";
        public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
    }
}
