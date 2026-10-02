cmake_minimum_required(VERSION 3.21)

project(Mambo VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(Qt6 6.8 REQUIRED COMPONENTS Quick QuickControls2)

qt_standard_project_setup(REQUIRES 6.8)

qt_add_executable(mambo WIN32
    src/app/main.cpp
    src/app/WindowBackdrop.cpp
    src/app/WindowBackdrop.h
    src/app/MotionPreferences.cpp
    src/app/MotionPreferences.h
)

set_source_files_properties(qml/theme/Theme.qml PROPERTIES
    QT_QML_SINGLETON_TYPE TRUE
)

qt_add_qml_module(mambo
    URI Mambo
    VERSION 1.0
    QML_FILES
        qml/theme/Theme.qml
        qml/shell/Main.qml
        qml/shell/TitleBar.qml
        qml/shell/Sidebar.qml
        qml/shell/ContentWell.qml
        qml/shell/WindowButton.qml
        qml/shell/Icon.qml
)

qt_add_resources(mambo "misans"
    PREFIX "/fonts"
    BASE "${CMAKE_CURRENT_SOURCE_DIR}/assets/fonts"
    FILES
        assets/fonts/MiSans-Regular.ttf
        assets/fonts/MiSans-Medium.ttf
        assets/fonts/MiSans-Semibold.ttf
        assets/fonts/MiSans-Bold.ttf
)

target_link_libraries(mambo PRIVATE
    Qt6::Quick
    Qt6::QuickControls2
)

if(WIN32)
    target_link_libraries(mambo PRIVATE dwmapi)
    target_compile_options(mambo PRIVATE /utf-8)
    target_link_options(mambo PRIVATE
        "/MANIFEST:EMBED"
        "/MANIFESTINPUT:${CMAKE_CURRENT_SOURCE_DIR}/src/app/mambo.manifest"
    )
endif()

install(TARGETS mambo)
