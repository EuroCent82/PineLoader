package pine.hello;

import java.lang.instrument.Instrumentation;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;

public final class Agent {
    private Agent() {
    }

    public static void premain(String arguments, Instrumentation instrumentation) throws Exception {
        var line = "HelloJava agent started." + System.lineSeparator();
        Files.writeString(Path.of(arguments), line, StandardOpenOption.CREATE, StandardOpenOption.APPEND);
    }
}
